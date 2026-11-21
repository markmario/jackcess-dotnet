using System;
using System.IO;
using System.Reflection;
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
        /// <summary>The user's Access workgroup file, which Access makes for a user the first time it runs.</summary>
        public static readonly string Workgroup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Access", "System.mdw");

        /// <summary>
        /// DAO's engine - ACE's (DAO.DBEngine.120) where it is installed, else Jet 4.0's (DAO.DBEngine.36) - given
        /// the user's workgroup file where there is one. Access keeps its system tables from a reader until it is
        /// given leave to read them, which takes a workgroup file. The engine takes the file only as it starts, and
        /// stays started while anything of it is held - in a 32-bit process it is shared with ACE's OLE DB
        /// provider - so an engine started without it keeps the system tables from every later reader in the
        /// process. Every engine that may start it first names the file.
        /// </summary>
        public static object NewEngine()
        {
            var dbeType = Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: false) ?? Type.GetTypeFromProgID("DAO.DBEngine.36", throwOnError: true);
            var dbe = Activator.CreateInstance(dbeType);
            if (File.Exists(Workgroup))
            {
                dbeType.InvokeMember("SystemDB", BindingFlags.SetProperty, null, dbe, new object[] { Workgroup });
                dbeType.InvokeMember("DefaultUser", BindingFlags.SetProperty, null, dbe, new object[] { "Admin" });
                dbeType.InvokeMember("DefaultPassword", BindingFlags.SetProperty, null, dbe, new object[] { "" });
            }
            return dbe;
        }

        // TRIAL (#145, removed before review): which way of using DAO a run tries.
        internal static readonly string Trial = Environment.GetEnvironmentVariable("JACKCESS_DAO_TRIAL") ?? "baseline";

        public static void Release(object engine)
        {
            if (Trial == "shared" && ReferenceEquals(engine, _shared)) return;
            Collect();
            if (Trial != "nofinal" && engine != null && Marshal.IsComObject(engine)) Marshal.FinalReleaseComObject(engine);
            Collect();
        }

        private static object _shared;

        /// <summary>TRIAL: the engine DaoRows reads with.</summary>
        internal static object EngineForReading()
        {
            if (Trial == "shared") return _shared ?? (_shared = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true)));
            return Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
        }

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
