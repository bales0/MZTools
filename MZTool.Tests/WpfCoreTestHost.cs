using System.Runtime.CompilerServices;
namespace MZTools.Tests;
internal static class WpfCoreTestHost
{
    [ModuleInitializer]
    internal static void Initialize() => WpfCoreAdapters.EnsureInitialized();
}
