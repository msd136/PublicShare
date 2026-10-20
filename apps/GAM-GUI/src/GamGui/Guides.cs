using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GamGui;

public sealed record HowTo(string Question, params string[] Steps);

public sealed record ScreenGuide(string Intro, HowTo[] HowTos, string[] Tips);

/// <summary>The "How to use this screen" text for every screen, keyed by screen title.</summary>
public static class Guides
{
    public static ScreenGuide? For(string screen) => All.TryGetValue(screen, out var g) ? g : null;

    private static readonly Dictionary<string, ScreenGuide> All = new()
    {
        ["Start"] = new(
            "Start here. The checklist confirms GAM is installed and can talk to Google; the tiles take you to each area of the app.",
            new[]
            {
                new HowTo("What does this app actually do?",
                    "Every button runs GAM, the command-line tool you installed, with the right options filled in.",
                    "Before anything changes, you see a plain-language summary and the exact GAM command, and nothing runs until you choose Run.",
                    "The dark strip at the bottom of the window always shows the last command that ran. History lists everything from this session."),
                new HowTo("The checklist shows a problem. What now?",
                    "Each failed item explains what's wrong and offers a button to fix it.",
                    "Fixes that need you to sign in to Google open a console window; follow its prompts, close it, then choose Run checks again."),
                new HowTo("Where do I do common jobs like offboarding someone?",
                    "Open Quick tasks. It has step-by-step jobs such as offboarding a user, finding inactive accounts and sharing a mailbox, each with a description of what it does."),
            },
            new[]
            {
                "Errors are explained in plain language with a suggested fix, right where they appear.",
                "Hide this guide with Hide guide; bring it back with Show guide.",
            }),

        ["Users"] = new(
            "Find, create and manage user accounts. Load the list, select one or more users, then pick an action on the right.",
            new[]
            {
                new HowTo("How do I find a user?",
                    "Type part of a name or address in the search box, for example jane or name:'Jane Doe', and press Enter.",
                    "To list one org unit, enter its path in the org unit box, for example /Sales.",
                    "After loading, the Filter rows box narrows the list instantly without asking Google again."),
                new HowTo("How do I create a user?",
                    "Choose New user and fill in the name, email address and org unit.",
                    "A strong random password is filled in for you. You'll see it again after the account is created so you can pass it on.",
                    "Leave \"Require a password change at next sign-in\" on so the user picks their own password."),
                new HowTo("How do I suspend or restore someone?",
                    "Select the users (Ctrl+click or Shift+click for several), then choose Suspend or Restore.",
                    "Suspended users can't sign in, but their data is kept."),
                new HowTo("How do I reset a password?",
                    "Select one user and choose Reset password. Keep the suggested password or type your own.",
                    "The new password is shown once afterwards; it's never written to the history or the command strip."),
                new HowTo("What does Delete do?",
                    "Delete removes the account. Google keeps deleted users restorable for a limited time from the Admin console, but plan data transfers first. Quick tasks > Offboard a user covers that."),
            },
            new[]
            {
                "Double-click a user to see all of their details.",
                "Export CSV saves the current list (including any filter) for a spreadsheet.",
            }),

        ["Groups"] = new(
            "Browse groups on the left. Selecting a group shows its members on the right, where you can add, remove or change roles.",
            new[]
            {
                new HowTo("How do I add people to a group?",
                    "Load groups and select the group.",
                    "Choose Add members, paste one or more email addresses (separated by commas, spaces or new lines) and pick a role."),
                new HowTo("What's the difference between member, manager and owner?",
                    "Members receive the group's email and can access what's shared with the group.",
                    "Managers can also add and remove members.",
                    "Owners can do everything, including changing the group's settings and deleting it."),
                new HowTo("How do I find which groups a user is in?",
                    "Use Quick tasks > Groups a user belongs to."),
            },
            new[]
            {
                "Search with email:sales* to find every group whose address starts with sales.",
                "Group details shows the group's settings, such as who can post and join.",
            }),

        ["Org units"] = new(
            "Your organization's structure as a tree. Settings in the Admin console apply per org unit, so where a user sits decides what they can use.",
            new[]
            {
                new HowTo("How do I move users into an org unit?",
                    "Load org units and select the destination in the tree.",
                    "Choose Move users here and paste their email addresses.",
                    "You can also move users from the Users screen with Move to org unit."),
                new HowTo("How do I create an org unit?",
                    "Select the parent in the tree (or the top level), choose New org unit here and give it a name."),
                new HowTo("Why can't I delete an org unit?",
                    "Google only deletes empty org units. Move its users, devices and child org units somewhere else first."),
            },
            new[] { "Show users lists the accounts in the selected org unit." }),

        ["Devices"] = new(
            "ChromeOS devices and mobile devices managed by your domain, on separate tabs. Load the list, select devices, then choose an action.",
            new[]
            {
                new HowTo("How do I find a specific Chromebook?",
                    "Search by serial number with id:SERIAL, by user with user:jane, or by status with status:provisioned.",
                    "Quick tasks > Find a Chromebook by serial number does this with a single box."),
                new HowTo("A Chromebook was lost or stolen. What should I do?",
                    "Select it and choose Disable. It shows a message on screen and can't be used until you choose Re-enable."),
                new HowTo("What's the difference between Disable and Deprovision?",
                    "Disable locks the device but keeps it managed.",
                    "Deprovision removes it from management entirely, for example before selling or recycling it; it must be re-enrolled to be managed again."),
                new HowTo("What do the mobile wipe options do?",
                    "Wipe account removes only the work account and its data, leaving personal data on the phone.",
                    "Wipe device erases everything on the phone. Use it only for company-owned devices."),
            },
            new[] { "Double-click a device to see all of its details." }),

        ["Drive & Gmail"] = new(
            "Look at one user's Drive files and Gmail settings, and change their vacation reply, forwarding or delegates. This uses GAM's service account.",
            new[]
            {
                new HowTo("How do I see a user's files or mail?",
                    "Enter the user's email address, pick what to view from the list and choose Show.",
                    "For Drive files and Gmail messages, an optional search box appears. It uses the same search syntax as Drive and Gmail."),
                new HowTo("How do I set an out-of-office reply for someone?",
                    "Enter their address and choose Vacation responder.",
                    "Fill in a subject and message. \"Only reply to people in my organization\" keeps the reply internal."),
                new HowTo("How do I hand a leaver's files to their manager?",
                    "Enter the leaver's address and choose Transfer Drive files. Google's transfer service moves ownership in the background and emails both people when it finishes."),
                new HowTo("It says the service account isn't authorized.",
                    "Drive and Gmail access needs GAM's service account with domain-wide delegation.",
                    "Open Settings > Check the service account; GAM lists any missing scopes and where to add them."),
            },
            new[] { "Delegates can read, send and delete mail in the mailbox, so grant it carefully." }),

        ["Quick tasks"] = new(
            "Ready-made jobs for things admins do often. Each card says what it does and which GAM command it uses. Choose Start, answer a few questions, and review before anything runs.",
            new[]
            {
                new HowTo("How do I run a task?",
                    "Find it by category or with the search box, then choose Start.",
                    "Fill in the questions (they're explained in the form).",
                    "Tasks that change something show a confirmation with the exact commands first. Reports just run."),
                new HowTo("Where do the results go?",
                    "Reports appear in the results area at the bottom of this screen, where you can filter, copy or export them.",
                    "Tasks that change things show a summary of each step afterwards."),
                new HowTo("I need something that isn't here.",
                    "All commands has every GAM command with a form to fill in. Ask your GAM-savvy colleague, or check the GAM wiki for the command name, then search for it there."),
            },
            new[] { "Cards marked \"Makes changes\" change data in your domain; the others only read." }),

        ["All commands"] = new(
            "Every command GAM has, each with a form generated from GAM's own reference. Use this when Quick tasks and the other screens don't cover what you need.",
            new[]
            {
                new HowTo("How do I find a command?",
                    "Type what you want to do in the search box, such as vacation, print courses or license, and pick a command from the list.",
                    "Commands are grouped the same way as GAM's documentation."),
                new HowTo("How do I fill in the form?",
                    "Bold green words are fixed parts of the command. Dropdowns let you pick between alternatives.",
                    "Tick a check box to include an optional part; its own fields appear below it.",
                    "Hover over any box to see exactly what it accepts. Boxes with a Guided button can be filled in step by step.",
                    "The command line underneath updates as you type, and anything still missing is listed in red."),
                new HowTo("Some commands open a browser or ask questions.",
                    "Use Open in terminal for those; it runs the command in a console window where you can answer GAM's prompts."),
            },
            new[] { "Edit in console copies the command line to the Console screen if you want to tweak it by hand." }),

        ["Console"] = new(
            "For people who know GAM's commands: type one exactly as you would at a command prompt, without the word gam.",
            new[]
            {
                new HowTo("How do I repeat an earlier command?",
                    "Press Up and Down in the command box to step through your history. It's kept between sessions."),
                new HowTo("GAM is asking me a question.",
                    "Type your answer in the box under the command and press Enter.",
                    "If GAM needs to open a browser, use Open in terminal instead."),
                new HowTo("Where's the output?",
                    "Output streams into Live output as GAM runs. When it finishes, CSV output from print commands becomes a table below."),
            },
            new[] { "Quoting works as at a command prompt: name:'Jane Doe' or \"two words\"." }),

        ["Settings"] = new(
            "Tell the app where GAM is, check that it's working, and run GAM's one-time setup.",
            new[]
            {
                new HowTo("Setting up GAM for the first time",
                    "Install GAM7 (it installs to C:\\GAM7 by default; this app finds it there).",
                    "Run the three first-time setup steps in order. Each opens a console window and your browser; follow GAM's prompts.",
                    "Step 3 needs any user in your domain and confirms the Drive and Gmail features will work."),
                new HowTo("How do I know it's working?",
                    "Use the checks: Check connection tests the network, Admin authorization shows who GAM signs in as and what it can do, and Domain info proves the whole path works."),
                new HowTo("I manage more than one domain.",
                    "Give each domain its own GAM config folder and switch between them with Config folder (GAMCFGDIR), then Save settings."),
            },
            new[] { "Settings are saved for your Windows account only." }),
    };
}

/// <summary>The collapsible "How to use this screen" panel shown under a screen's heading.</summary>
public sealed class GuidePanel : Border
{
    public GuidePanel(ScreenGuide guide)
    {
        Background = Ui.Brush("GuideBrush");
        BorderBrush = Ui.Brush("AccentSoftBrush");
        BorderThickness = new Thickness(3, 1, 1, 1);
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(16, 12, 16, 10);
        Margin = new Thickness(0, 10, 0, 4);

        var stack = new StackPanel();
        stack.Children.Add(Ui.Text("How to use this screen", "H2"));
        stack.Children.Add(Ui.Margin(Ui.Text(guide.Intro), 0, 4, 0, 6));

        foreach (var h in guide.HowTos)
        {
            var steps = new StackPanel { Margin = new Thickness(22, 2, 0, 6) };
            for (int i = 0; i < h.Steps.Length; i++)
            {
                var row = Ui.Grid("auto", h.Steps.Length > 1 ? "22,*" : "0,*");
                row.Add(Ui.Text(h.Steps.Length > 1 ? $"{i + 1}." : "", "Muted").At(0, 0), Ui.Text(h.Steps[i]).At(0, 1));
                row.Margin = new Thickness(0, 2, 0, 0);
                steps.Children.Add(row);
            }
            stack.Children.Add(new Expander
            {
                Header = new TextBlock { Text = h.Question, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("AccentBrush") },
                Content = steps,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        if (guide.Tips.Length > 0)
        {
            var tips = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var t in guide.Tips)
                tips.Children.Add(Ui.Text("Tip: " + t, "Muted", 12.5));
            stack.Children.Add(tips);
        }
        Child = stack;
    }
}
