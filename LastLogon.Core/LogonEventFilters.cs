namespace LastLogon.Core;

public enum LogonAccountFilter
{
    All,
    Local,
    Domain
}

public enum LogonTypeFilter
{
    All,
    Interactive,
    Network,
    Service,
    Other
}
