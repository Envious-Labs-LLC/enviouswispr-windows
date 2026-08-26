namespace EnviousWispr.Core.Diagnostics;

public interface IAppLogger
{
    void Write(AppLogEntry entry);
}
