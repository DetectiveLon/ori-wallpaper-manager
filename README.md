# Oriole

A clean-room Windows wallpaper manager. **Foundation build only — not yet installable.**

## Status

The `foundation/0.1.0` branch contains the initial source package. Its first source commit added 27 project files:

- A native WPF interface with six sections.
- A Wallhaven API client with categories, tags, colours, resolutions, sorting and pagination.
- A sequential scheduling engine designed to avoid overlapping changes and timer backlogs.
- Initial unit tests and a Windows GitHub Actions CI workflow.
- Product, architecture and clean-room provenance documentation.

The source is available on [the foundation branch](https://github.com/DetectiveLon/oriole-wallpaper-manager/tree/foundation/0.1.0). Draft PR #1 is open for review; it has not been merged.

## Not implemented yet

- Windows wallpaper application layer, monitor detection and assignment
- Functional image browser and local library
- Tray controls
- Installer

Images fetched from Wallhaven remain subject to their original owners' rights.

## Repository layout

`main` retains the initial project-status README. The working source is developed on separate branches and proposed through pull requests.
