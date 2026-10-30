using System.Runtime.CompilerServices;
namespace MZTools.Capture.Windows.Tests;
internal static class WpfCoreTestHost
{
    [ModuleInitializer]
    internal static void Initialize() => WpfCoreAdapters.EnsureInitialized();
}
