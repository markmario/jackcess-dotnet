using System;
using System.Runtime.InteropServices;

namespace JackcessDotNet.AccessTests.Support
{
    /// <summary>
    /// Lets go of a DAO engine a test made, and of everything the test got from it.
    ///
    /// Releasing the engine alone leaves it alive: the databases, recordsets, table definitions and fields it handed
    /// out each hold it, and .NET lets go of those only when it next collects. In the Access Database Engine 2016
    /// redistributable - what CI installs - an engine left alive that way, then an OLE DB connection that writes to a
    /// .accdb file, then a second engine, is an access violation inside Office's shared library: the test saw
    /// RPC_E_SERVERFAULT from OpenDatabase, and the next DAO call in the process never returned. Access itself was
    /// the whole of it - the same steps with the engine under test writing nothing failed the same way - and
    /// Microsoft 365's Access does not do it. So every wrapper is collected before the engine goes, and after.
    ///
    /// A wrapper still held in a local when this is called is not collected in a Debug build, where a local lives to
    /// the end of its method; the tests are built and run in Release.
    /// </summary>
    internal static class Dao
    {
        public static void Release(object engine)
        {
            Collect();
            if (engine != null && Marshal.IsComObject(engine)) Marshal.FinalReleaseComObject(engine);
            Collect();
        }

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
