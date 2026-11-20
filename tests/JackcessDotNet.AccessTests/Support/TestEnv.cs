using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

// DELIBERATELY SERIAL. The native ACE OLE DB provider fails under concurrent OleDbConnection.Open calls in one process
// ("Non-NULL controlling IUnknown was specified..."), even against separate database files. This is a property of the
// provider, not of test isolation: every test already uses its own temporary folder. scripts/Invoke-AccessTests.ps1
// runs test classes side by side in processes of their own instead.
// xUnit.net runs test classes in parallel by default, so this is what keeps it serial.
[assembly: Parallelization(Mode = ParallelMode.None)]
[assembly: AssemblyFixture(typeof(JackcessDotNet.AccessTests.Support.TestEnv))]
[assembly: JackcessDotNet.AccessTests.Support.RequiresAccess]

namespace JackcessDotNet.AccessTests.Support
{
    /// <summary>
    /// Created once, before any test runs. xunit.runner.json sets the culture of the threads tests run on; this
    /// sets the default for any other thread.
    /// </summary>
    public sealed class TestEnv
    {
        public TestEnv()
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        }
    }

    /// <summary>Per-test working directory; deleted on dispose.</summary>
    public sealed class TempDb : IDisposable
    {
        public string Dir { get; }

        public TempDb()
        {
            Dir = Path.Combine(Path.GetTempPath(), "jackcess-access-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
        }

        /// <summary>Path for a database that does not exist yet (save-target).</summary>
        public string NewMdbPath()
        {
            return Path.Combine(Dir, Guid.NewGuid().ToString("N") + ".mdb");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, true); }
            catch { /* best effort; OS temp cleanup will get stragglers */ }
        }
    }

    /// <summary>
    /// Every test here needs Microsoft Access's own engine - the ACE OLE DB provider and DAO, from the Access Database
    /// Engine redistributable or from Access itself, of this process's bitness (64-bit) - as the referee. On a machine
    /// without them each test is skipped, saying why. Where <c>JACKCESS_ACCESS_REQUIRED=1</c> (CI sets it), a missing
    /// referee fails every test instead: there it means the install went wrong, and a skip would pass unrefereed.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class RequiresAccessAttribute : BeforeAfterTestAttribute
    {
        /// <summary>The environment variable that makes a missing referee a failure.</summary>
        public const string RequiredVariable = "JACKCESS_ACCESS_REQUIRED";

        private static readonly Lazy<string> WhyMissing = new Lazy<string>(Probe);

        public override void Before(System.Reflection.MethodInfo methodUnderTest, IXunitTest test)
        {
            var why = WhyMissing.Value;
            if (why == null) return;
            if (Environment.GetEnvironmentVariable(RequiredVariable) == "1")
                throw new InvalidOperationException(RequiredVariable + "=1, and Access's engine is not here to referee: " + why);
            Assert.Skip("Access's engine is not on this machine, so there is no referee here (" + why + "). Install the Access "
                + "Database Engine 2016 redistributable (x64), or Access; see docs/building-and-testing.md.");
        }

        private static string Probe()
        {
            if (!Environment.Is64BitProcess) return "the tests run in a 32-bit process; they are built for x64";
            foreach (var progId in new[] { AceVerification.ProviderName, "DAO.DBEngine.120" })
            {
                // A ProgID can be registered with its DLL gone, so the object is made, not only looked up.
                var type = Type.GetTypeFromProgID(progId, throwOnError: false);
                if (type == null) return progId + " is not registered";
                try
                {
                    var instance = Activator.CreateInstance(type);
                    if (instance != null && System.Runtime.InteropServices.Marshal.IsComObject(instance))
                        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(instance);
                }
                catch (Exception e)
                {
                    return progId + " is registered and cannot be made: " + e.Message;
                }
            }
            return null;
        }
    }
}
