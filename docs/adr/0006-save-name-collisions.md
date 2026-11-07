# Save-name collisions in the desktop library

Status: implemented in C7, reviewed/rebased 2026-10-30.

Saved builds have stable record IDs; names are display metadata. Normal Save on
an already saved build replaces that ID using its last known version, even after
a rename. Save as new deliberately creates another record, including when its
name is already used. Blank names retain the existing create behavior.

For the first normal Save of a named new/imported build, refresh the library and
look for the same trimmed, ordinal case-insensitive name in the same pack. Real
unreadable records have no reliable name/pack metadata: if any are present, stop
and explain that name matching cannot be checked. An explicit Save as new may
create a copy while preserving those records; never pretend to identify their
old names or overwrite them through matching.

With readable metadata, no match creates a record and one match offers Replace
confirmation. For several matches, the first Save clears any stale picker
selection and asks for a new choice. That choice is scoped to the captured build;
other library actions and build changes expire it. The user selects a target and
retries Save; confirmation describes the copy just selected and consumes that
choice, even when cancelled. Never choose an arbitrary duplicate. Exact changes
in the stored name, including case or spacing, are disclosed before replacement.
Declining, closing, or lacking a dialog host performs no write; the no-host advice
offers Save as new without asking the user to discard their work by opening an
old build.

Replacement uses the version listed before confirmation. A concurrent write or
deletion remains a conflict with a name-based message about the confirmation;
do not refresh the approved version or fall back to creation. The captured build
is what gets saved; later edits remain dirty against that saved baseline.

A first named Save updates its fresh listing with the returned saved record,
avoiding a second scan. This view is not an atomic snapshot of other writers;
explicit Refresh remains the route for their subsequent changes. Names are not
storage-level unique: independent creates may race, explicit copies may share a
name, and renaming an existing ID may produce duplicates. Existing version checks,
writer locks and atomic replacement remain authoritative. No storage schema or
Go-library changes. The finding assessment is an internal review record.
