# Oriole

A clean-room Windows wallpaper manager. **Foundation build only — not yet installable.**

## Status

Oriole 0.1.0 is the initial source package (29 files). It includes:

- A native WPF interface with six sections.
- A Wallhaven API client with categories, tags, colours, resolutions, sorting and pagination.
- A sequential scheduling engine that avoids overlapping changes and timer backlogs.
- Initial unit tests and a Windows GitHub Actions CI workflow.
- Product, architecture and clean-room provenance documentation.

## Not implemented yet

- Windows wallpaper application layer, monitor detection and assignment
- Functional image browser and local library
- Tray controls
- Installer

Images fetched from Wallhaven remain subject to their original owners' rights.

## Repository layout

`main` holds the initialized README. The clean-room source is intended to land on a separate branch.
