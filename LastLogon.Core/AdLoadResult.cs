namespace LastLogon.Core;

/// <summary>
/// What a search of Active Directory for computer names came to: the names to put in the list
/// (none when the search failed, was cancelled or found nothing, so that the list the operator
/// typed is left alone) and one sentence that says what happened.
/// </summary>
public sealed record AdLoadResult(IReadOnlyList<string> Names, string Message);
