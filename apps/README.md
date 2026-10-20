# Apps

Self-contained utilities and small applications. Each subfolder is an independent project with its own source, build configuration, and README.

## Index

| App | Purpose | Platform |
|---|---|---|
| [PrepareForTesting](./PrepareForTesting) | Closes Outlook, Teams, Chrome, Office, etc. before a proctored test, then shows a confirmation dialog. | Windows (.NET 8) |
| [HelpdeskHeroes](./HelpdeskHeroes) | Native Windows app that collects user and computer data and allows for quick contact of the helpdesk, FAQs and other resources. | Windows (.NET 8) |
| [GAMGUI](./GAM-GUI) | A Windows (WPF, .NET 8) desktop front end for [GAM7](https://github.com/GAM-team/GAM) | Windows (.NET 8) |

<!-- Add new entries above this line as new apps are added. -->

## Structure

```
apps/
├── README.md                  # This file
├── PrepareForTesting/         # Each app gets its own folder
│   ├── README.md              # Build / install / usage instructions
│   ├── Program.cs
│   └── PrepareForTesting.csproj
└── <NextApp>/
    └── ...
```

Each app folder is intended to be self-contained: clone the repo (or download just one folder), follow that app's README, and you have a working tool.

