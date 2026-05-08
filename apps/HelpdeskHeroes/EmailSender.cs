using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HelpdeskHeroes;

/// <summary>
/// Sends helpdesk tickets via the SMTP2GO HTTP API. Replaces the previous
/// Outlook-COM + mailto path: the user's machine no longer needs Outlook
/// configured, no Office interop, no profile setup. The user also never
/// sees the message before it leaves — we POST and we're done.
///
/// Strategy:
///   1. Read the API key + sender domain from the registry (Configuration.cs).
///      The remediation script writes these at install time. If the key is
///      missing we surface a "deployment misconfigured" error rather than
///      pretending we sent it — the SentPage already has a "copy this
///      manually" view that handles that case.
///   2. Resolve the user's email via UserIdentity so the From line is
///      personal — replies go straight back to the user.
///   3. POST JSON to https://api.smtp2go.com/v3/email/send with a 15-second
///      hard timeout. Network on end-user devices is often slow but rarely
///      hangs forever; 15s is well under what feels broken.
///   4. Surface real errors (403 invalid key, 4xx domain not authorized,
///      etc.) in <see cref="SendResult.ErrorDetail"/> so the SentPage's
///      diagnostics block is actually useful for the helpdesk to see what
///      went wrong on a misconfigured device.
///
/// The Outlook + mailto fallbacks are gone. With Outlook removed there's no
/// reasonable fallback: if SMTP2GO isn't reachable we show the "copy this"
/// view (already implemented in SentPage). That is dramatically less noisy
/// than handing the user a half-broken Outlook profile launcher.
/// </summary>
internal static class EmailSender
{
    /// <summary>How the message ended up after <see cref="SendAsync"/> returned.</summary>
    internal enum SendStatus
    {
        /// <summary>SMTP2GO accepted the email — message is queued for delivery.</summary>
        Sent,

        /// <summary>Couldn't deliver — user must copy the ticket manually.</summary>
        Failed
    }

    /// <summary>Detail bundle returned by <see cref="SendAsync"/>.</summary>
    internal sealed record SendResult(SendStatus Status, string? ErrorDetail = null)
    {
        public bool IsAutoSent => Status == SendStatus.Sent;
    }

    private const string ApiUrl = "https://api.smtp2go.com/v3/email/send";
    private const int TimeoutSeconds = 15;

    /// <summary>
    /// Single shared HttpClient — the .NET guidance is to reuse one instance
    /// per process to avoid socket exhaustion. The per-request CancellationToken
    /// gives us per-call timeout control instead of relying on the client-wide
    /// HttpClient.Timeout.
    /// </summary>
    private static readonly HttpClient SharedClient = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds + 5)
        };
        c.DefaultRequestHeaders.Accept.Clear();
        c.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"HelpdeskHeroes/{SystemInfo.AppVersion}");
        return c;
    }

    /// <summary>
    /// Convenience sync wrapper — kept so callers that aren't async-aware
    /// (Quick ticket flow on MainForm) don't have to be rewritten. Internally
    /// it just GetAwaiter().GetResult()s the async path.
    /// </summary>
    public static SendResult Send(
        string to, string subject, string textBody, string? htmlBody = null,
        AttachmentSet? attachments = null)
    {
        // ConfigureAwait(false) on the inner Task so we don't deadlock the
        // UI message loop if a caller invokes us from the UI thread without
        // marshalling. Callers that want responsive UI should use SendAsync.
        return SendAsync(to, subject, textBody, htmlBody, attachments)
            .ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Send a single helpdesk ticket, optionally with an HTML body and file
    /// attachments. Always returns a <see cref="SendResult"/>; callers should
    /// branch on <see cref="SendStatus"/> for messaging.
    ///
    /// <para>Body handling: SMTP2GO accepts <c>text_body</c>, <c>html_body</c>,
    /// or both. When both are sent, the receiving mail client picks based on
    /// its own preferences (Outlook nearly always picks HTML). We always send
    /// the plain-text version as a safety net for forwarders / text-only
    /// relays / accessibility tools, even though no current user-facing
    /// flow calls in with HTML disabled.</para>
    /// </summary>
    public static async Task<SendResult> SendAsync(
        string to, string subject, string textBody, string? htmlBody = null,
        AttachmentSet? attachments = null)
    {
        // ---- Pre-flight: configuration ----
        string? apiKey = Configuration.Smtp2GoApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new SendResult(
                SendStatus.Failed,
                "SMTP2GO API key not configured. Ask the helpdesk to push the " +
                "Helpdesk Heroes config policy to this device.");
        }

        // ---- Pre-flight: who's the user? ----
        string fromAddress;
        try
        {
            fromAddress = UserIdentity.ResolveEmail();
        }
        catch (Exception ex)
        {
            return new SendResult(
                SendStatus.Failed,
                $"Couldn't determine sender address: {ex.Message}");
        }

        // ---- Build SMTP2GO payload ----
        // We use the resolved user's email for both sender and as the
        // Reply-To so helpdesk replies land in the user's inbox even if
        // the relay rewrites the envelope sender.
        //
        // Built as a Dictionary so we can conditionally add the attachments
        // array (SMTP2GO ignores empty arrays but rejects malformed ones,
        // and an absent key is the safer default for the no-attachments path).
        var payload = new Dictionary<string, object?>
        {
            ["api_key"]   = apiKey,
            ["sender"]    = fromAddress,
            ["to"]        = new[] { to },
            ["subject"]   = subject,
            ["text_body"] = textBody,
            ["custom_headers"] = new object[]
            {
                new { header = "Reply-To",   value = fromAddress },
                new { header = "X-Mailer",   value = $"HelpdeskHeroes/{SystemInfo.AppVersion}" },
                new { header = "X-Computer", value = SystemInfo.ComputerName }
            }
        };

        // Add the HTML body when supplied. SMTP2GO assembles the message
        // as multipart/alternative when both bodies are present, which is
        // the right wire format for letting the recipient's client pick.
        if (!string.IsNullOrEmpty(htmlBody))
        {
            payload["html_body"] = htmlBody;
        }

        // ---- Attachments ----
        // Read + base64-encode each file lazily here, on the same thread
        // that's about to POST. We do NOT hold the file bytes longer than
        // we have to: the byte[] is dropped as soon as Convert.ToBase64String
        // returns, so peak memory per file is ~(raw + base64) ≈ raw × 2.33.
        // SMTP2GO's docs name the field "filename" and "fileblob".
        if (attachments != null && attachments.Count > 0)
        {
            var encoded = new List<object>(attachments.Count);
            foreach (var a in attachments.Items)
            {
                try
                {
                    byte[] raw = File.ReadAllBytes(a.FullPath);
                    string b64 = Convert.ToBase64String(raw);
                    encoded.Add(new
                    {
                        filename = a.DisplayName,
                        fileblob = b64,
                        mimetype = GuessMimeType(a.DisplayName)
                    });
                }
                catch (Exception ex)
                {
                    // One unreadable attachment shouldn't kill the whole
                    // ticket — surface it in the error detail and bail out
                    // so the user knows which file was the problem.
                    return new SendResult(
                        SendStatus.Failed,
                        $"Couldn't read attachment \"{a.DisplayName}\": {ex.Message}");
                }
            }
            payload["attachments"] = encoded;
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(payload);
        }
        catch (Exception ex)
        {
            return new SendResult(SendStatus.Failed, $"Couldn't build email payload: {ex.Message}");
        }

        // ---- POST ----
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            using var resp = await SharedClient.SendAsync(req, cts.Token).ConfigureAwait(false);
            string respBody = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                // SMTP2GO returns HTTP 200 even for some payload-level errors —
                // success is signalled by data.succeeded == 1 in the body.
                if (LooksLikeSmtp2GoSuccess(respBody))
                {
                    return new SendResult(SendStatus.Sent);
                }
                return new SendResult(
                    SendStatus.Failed,
                    $"SMTP2GO rejected the message: {Truncate(respBody, 300)}");
            }

            return new SendResult(
                SendStatus.Failed,
                $"SMTP2GO returned HTTP {(int)resp.StatusCode}: {Truncate(respBody, 300)}");
        }
        catch (TaskCanceledException) when (cts.IsCancellationRequested)
        {
            return new SendResult(
                SendStatus.Failed,
                $"Send timed out after {TimeoutSeconds}s. Check your internet connection.");
        }
        catch (HttpRequestException ex)
        {
            return new SendResult(
                SendStatus.Failed,
                $"Network error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new SendResult(
                SendStatus.Failed,
                $"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Light-touch parse of the SMTP2GO response — we don't deserialize the
    /// whole envelope because the API surface is well-documented but small.
    /// SMTP2GO success body looks like: {"data":{"succeeded":1,"failed":0,...}}
    /// </summary>
    private static bool LooksLikeSmtp2GoSuccess(string body)
    {
        if (string.IsNullOrEmpty(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return false;
            if (!data.TryGetProperty("succeeded", out var succeeded)) return false;
            return succeeded.GetInt32() >= 1;
        }
        catch
        {
            // Body wasn't JSON or didn't have the expected shape — treat as failure
            // so we don't claim a send when we can't actually verify it.
            return false;
        }
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s.Substring(0, max - 1) + "…";
    }

    /// <summary>
    /// Cheap extension → MIME map. SMTP2GO will accept "application/octet-stream"
    /// for unknown types, but giving it a real type lets the recipient's mail
    /// client preview screenshots inline. We only enumerate the formats
    /// users actually attach (snips, photos, PDFs, Office docs, logs).
    /// </summary>
    private static string GuessMimeType(string filename)
    {
        string ext = Path.GetExtension(filename).ToLowerInvariant();
        return ext switch
        {
            ".png"  => "image/png",
            ".jpg"  => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".gif"  => "image/gif",
            ".bmp"  => "image/bmp",
            ".webp" => "image/webp",
            ".heic" => "image/heic",
            ".pdf"  => "application/pdf",
            ".txt"  => "text/plain",
            ".log"  => "text/plain",
            ".csv"  => "text/csv",
            ".html" => "text/html",
            ".htm"  => "text/html",
            ".doc"  => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls"  => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt"  => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".zip"  => "application/zip",
            _       => "application/octet-stream"
        };
    }
}
