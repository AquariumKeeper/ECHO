namespace CharacterPlanner.Core.Diagnostics;

public sealed record PackDiagnostic(string Severity, string Code, string Path, string Message);
