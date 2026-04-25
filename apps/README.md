# Apps

Self-contained utilities and small applications. Each subfolder is an independent project with its own source, build configuration, and README.

## Index

| App | Purpose | Platform |
|---|---|---|
| [PrepareForTesting](./PrepareForTesting) | Closes Outlook, Teams, Chrome, Office, etc. before a proctored test, then shows a confirmation dialog. | Windows (.NET 8) |

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

## Conventions for new apps

When adding a new app under this folder, please include:

- A `README.md` at the root of the app folder explaining what it does, prerequisites, how to build or run it, and any deployment notes.
- All source and build configuration needed to produce the deliverable from a clean checkout.
- A short entry in the index table above (name, one-line purpose, platform).

Keep each app's dependencies and tooling local to its folder. Don't introduce shared build infrastructure across apps unless there's a clear reason — independence makes it easy to share a single app without dragging in unrelated machinery.
