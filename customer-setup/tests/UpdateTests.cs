using System;

internal static class UpdateTests
{
    private static int Main()
    {
        int failed = UpdateProtocolTests.Run();
        failed += UpdatePackageTests.Run();
        failed += UpdateCoordinatorTests.Run();
        failed += UpdateEnvironmentTests.Run();
        failed += UpdateProfileTests.Run();
        failed += UpdateHandoffTests.Run();
        Console.WriteLine("UPDATER RESULT " + failed + " failed. Only isolated fixtures were changed; no live application, service, trust or enrollment was changed.");
        return failed == 0 ? 0 : 1;
    }
}
