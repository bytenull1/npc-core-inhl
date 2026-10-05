# Changelog

Changes in this fork relative to NPC.Core 1.0.1.

## Unreleased

### Fixed
- Object-use planning rejects partial routes that end short of the selected approach node.
- A reachable alternative approach node can be selected when the nearest candidate cannot be reached.
- Approach replanning applies the same endpoint check.

### Added
- Regression checks for complete, partial, empty and wrong-deck approach routes.

Public API signatures are unchanged.
