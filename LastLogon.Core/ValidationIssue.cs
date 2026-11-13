namespace LastLogon.Core;

/// <summary>The input field a validation message belongs to.</summary>
public enum ValidationField { Computers, Username, TimeRange }

/// <summary>An input problem that stopped a scan from starting, and the field it is about.</summary>
public sealed record ValidationIssue(ValidationField Field, string Message);
