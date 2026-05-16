using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace ThanyMarcus.Portal.Tests.SagaWorker;

internal static class SagaTestDp
{
    public static IDataProtectionProvider Create(string keysDir)
    {
        Directory.CreateDirectory(keysDir);
        return new ServiceCollection()
            .AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
            .SetApplicationName("ThanyMarcus.Portal")
            .Services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>();
    }
}
