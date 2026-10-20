using System.Windows;
using System.Windows.Controls;
using GamGui.Core;

namespace GamGui.Views;

public sealed class DevicesView : ViewBase
{
    // ChromeOS
    private readonly HintBox _crosQuery = new("Search, e.g. status:provisioned or user:jane", 300);
    private readonly HintBox _crosOu = new("Limit to org unit, e.g. /Classrooms", 200);
    private readonly ResultView _cros = new("Choose Load devices to list ChromeOS devices.");

    // Mobile
    private readonly HintBox _mobQuery = new("Search, e.g. email:jane or model:pixel", 300);
    private readonly ResultView _mobile = new("Choose Load devices to list mobile devices.");

    private static readonly string[] CrosIdCols = { "deviceId" };
    private static readonly string[] MobileIdCols = { "resourceId" };

    private static readonly string[] DeprovisionReasons =
    {
        "deprovision_retiring_device",
        "deprovision_same_model_replace",
        "deprovision_different_model_replace",
        "deprovision_upgrade_transfer",
    };

    public DevicesView()
    {
        var tabs = new TabControl { Padding = new Thickness(10) };
        tabs.Items.Add(new TabItem { Header = "ChromeOS devices", Content = BuildCros() });
        tabs.Items.Add(new TabItem { Header = "Mobile devices", Content = BuildMobile() });

        Content = Ui.Grid("auto,*").Add(
            Header("Devices", "ChromeOS and mobile devices managed by your domain."),
            tabs.At(1));
    }

    private UIElement BuildCros()
    {
        _crosQuery.OnEnter(async () => await LoadCrosAsync());
        var toolbar = Ui.Wrap(
            Ui.Margin(_crosQuery, 0, 0, 6, 6), Ui.Margin(_crosOu, 0, 0, 6, 6),
            Ui.Button("Load devices", async (_, _) => await LoadCrosAsync(), "PrimaryButton"));
        var actions = Ui.Wrap(
            Ui.Button("Details", async (_, _) => await CrosDetailsAsync(), tip: "Shows everything about the selected device. Runs: gam info cros|mobile <id>"),
            Ui.Button("Move to org unit…", async (_, _) => await CrosMoveAsync(), tip: "Runs: gam update cros <ids> ou <path>"),
            Ui.Button("Disable", async (_, _) => await CrosActionAsync("disable", "Disable"), tip: "Locks the device until re-enabled; it stays managed. Runs: gam update cros <ids> action disable"),
            Ui.Button("Re-enable", async (_, _) => await CrosActionAsync("reenable", "Re-enable"), tip: "Unlocks a disabled device. Runs: gam update cros <ids> action reenable"),
            Ui.Button("Deprovision…", async (_, _) => await CrosDeprovisionAsync(), "DangerButton", tip: "Removes devices from management. Runs: gam update cros <ids> action deprovision_… acknowledge_device_touch_requirement"));
        _cros.RowDoubleClick += async (_, _) => await CrosDetailsAsync();
        return Ui.Grid("auto,auto,*").Add(toolbar.At(0), actions.At(1), _cros.At(2));
    }

    private UIElement BuildMobile()
    {
        _mobQuery.OnEnter(async () => await LoadMobileAsync());
        var toolbar = Ui.Wrap(
            Ui.Margin(_mobQuery, 0, 0, 6, 6),
            Ui.Button("Load devices", async (_, _) => await LoadMobileAsync(), "PrimaryButton"));
        var actions = Ui.Wrap(
            Ui.Button("Details", async (_, _) => await MobileDetailsAsync(), tip: "Shows everything about the selected device. Runs: gam info cros|mobile <id>"),
            Ui.Button("Approve", async (_, _) => await MobileActionAsync("approve", "Approve"), tip: "Allows the device to sync work data. Runs: gam update mobile <ids> action approve doit"),
            Ui.Button("Block", async (_, _) => await MobileActionAsync("block", "Block"), tip: "Stops the device syncing work data. Runs: gam update mobile <ids> action block doit"),
            Ui.Button("Wipe account…", async (_, _) => await MobileActionAsync("admin_account_wipe",
                "Remove the work account and its data from"), tip: "Removes only the work account from the device. Runs: gam update mobile <ids> action admin_account_wipe doit"),
            Ui.Button("Wipe device…", async (_, _) => await MobileActionAsync("admin_remote_wipe",
                "Factory-reset (erase everything on)"), "DangerButton", tip: "Erases the whole device. Runs: gam update mobile <ids> action admin_remote_wipe doit"),
            Ui.Button("Delete…", async (_, _) => await MobileDeleteAsync(), "DangerButton", tip: "Removes the device record. Runs: gam delete mobile <ids> doit"));
        _mobile.RowDoubleClick += async (_, _) => await MobileDetailsAsync();
        return Ui.Grid("auto,auto,*").Add(toolbar.At(0), actions.At(1), _mobile.At(2));
    }

    // ---- ChromeOS ----

    private async Task LoadCrosAsync()
    {
        var args = new List<string> { "print", "cros" };
        if (_crosQuery.Value.Length > 0) { args.Add("query"); args.Add(_crosQuery.Value); }
        if (_crosOu.Value.Length > 0) { args.Add("limittoou"); args.Add(_crosOu.Value); }
        args.Add("fields");
        args.Add("deviceid,serialnumber,model,status,orgunitpath,annotateduser,lastsync,osversion");
        await RunAsync(new GamCommand(args), _cros);
    }

    private async Task CrosDetailsAsync()
    {
        var id = RequireSelection(_cros, "devices", CrosIdCols).FirstOrDefault();
        if (id != null) await ShowTextOf(GamCommand.Of("info", "cros", id), "ChromeOS device " + id);
    }

    private async Task CrosMoveAsync()
    {
        var ids = RequireSelection(_cros, "devices", CrosIdCols);
        if (ids.Count == 0) return;
        var v = FormDialog.Ask(OwnerWindow, "Move devices", $"{ids.Count} device(s) selected.", "Move",
            new Field("ou", "Org unit path", "/", Required: true));
        if (v == null) return;
        await After(await RunAsync(GamCommand.Of("update", "cros", string.Join(",", ids), "ou", v["ou"]),
            confirm: $"Move {ids.Count} device(s) to {v["ou"]}?"), LoadCrosAsync);
    }

    private async Task CrosActionAsync(string action, string verb)
    {
        var ids = RequireSelection(_cros, "devices", CrosIdCols);
        if (ids.Count == 0) return;
        await After(await RunAsync(GamCommand.Of("update", "cros", string.Join(",", ids), "action", action),
            confirm: $"{verb} {ids.Count} device(s)?"), LoadCrosAsync);
    }

    private async Task CrosDeprovisionAsync()
    {
        var ids = RequireSelection(_cros, "devices", CrosIdCols);
        if (ids.Count == 0) return;
        var v = FormDialog.Ask(OwnerWindow, "Deprovision devices",
            "Deprovisioned devices lose management and must be re-enrolled to be managed again.", "Choose",
            new Field("reason", "Reason", DeprovisionReasons[0], FieldKind.Choice, DeprovisionReasons));
        if (v == null) return;
        await After(await RunAsync(
            GamCommand.Of("update", "cros", string.Join(",", ids), "action", v["reason"], "acknowledge_device_touch_requirement"),
            confirm: $"Deprovision {ids.Count} device(s)?"), LoadCrosAsync);
    }

    // ---- Mobile ----

    private async Task LoadMobileAsync()
    {
        var args = new List<string> { "print", "mobile" };
        if (_mobQuery.Value.Length > 0) { args.Add("query"); args.Add(_mobQuery.Value); }
        args.Add("fields");
        args.Add("resourceid,deviceid,email,model,os,type,status,lastsync");
        await RunAsync(new GamCommand(args), _mobile);
    }

    private async Task MobileDetailsAsync()
    {
        var id = RequireSelection(_mobile, "devices", MobileIdCols).FirstOrDefault();
        if (id != null) await ShowTextOf(GamCommand.Of("info", "mobile", id), "Mobile device");
    }

    private async Task MobileActionAsync(string action, string verb)
    {
        var ids = RequireSelection(_mobile, "devices", MobileIdCols);
        if (ids.Count == 0) return;
        // GAM asks for "doit" on actions that can't be undone; the confirmation here replaces its prompt.
        await After(await RunAsync(GamCommand.Of("update", "mobile", string.Join(",", ids), "action", action, "doit"),
            confirm: $"{verb} {ids.Count} device(s)?"), LoadMobileAsync);
    }

    private async Task MobileDeleteAsync()
    {
        var ids = RequireSelection(_mobile, "devices", MobileIdCols);
        if (ids.Count == 0) return;
        await After(await RunAsync(GamCommand.Of("delete", "mobile", string.Join(",", ids), "doit"),
            confirm: $"Delete {ids.Count} device(s) from management?"), LoadMobileAsync);
    }

    private async Task After(GamResult? r, Func<Task> reload)
    {
        if (r == null) return;
        if (r.Success) await reload();
        else Dialogs.ShowResult(OwnerWindow, "GAM reported a problem", r);
    }
}
