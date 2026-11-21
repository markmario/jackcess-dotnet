using System;
using System.IO;
using System.Reflection;

namespace JackcessDotNet.AccessTests.Support
{
    /// <summary>
    /// DAO's engine, one for the whole test process, and the letting go of what a test got from it.
    ///
    /// A test used to make an engine of its own each time it asked DAO something and end it after - many a test, in
    /// one process, between ACE's OLE DB connections writing to the same files. In a 32-bit process the 2016
    /// redistributable's ACE 16 failed about one run in five of CursorsAndRowsInAccessTests' piece that makes one
    /// engine for each index at each of its checks: an access violation that ended the process (0xC0000005),
    /// 0xC000041D, or RPC_E_SERVERFAULT from a DAO call (#145). Measured on CI's 32-bit runners, that piece run again
    /// and again, three processes at a time: engines made and ended at once, as before, 6 of 30 runs died (two of
    /// 0xC0000005, four of 0xC000041D); engines left for .NET to end, 28 of 30 failed; one engine for the process, 2 of
    /// 90 died, both of 0xC000041D - the redistributable's own death, which Invoke-AccessTests.ps1 runs again - and
    /// none of 0xC0000005 or RPC_E_SERVERFAULT. The engine is therefore made once, the first time a test asks for it,
    /// and kept until the process ends; a test lets go of the databases, recordsets and the rest it got
    /// (<see cref="Release"/>), and never of the engine.
    ///
    /// Before that, in the same redistributable: an engine left alive by the wrappers it had handed out, then an OLE DB
    /// connection writing to an .accdb file, then a second engine, was an access violation inside Office's shared
    /// library - the test saw RPC_E_SERVERFAULT from OpenDatabase, and the next DAO call in the process never returned.
    /// Microsoft 365's Access does not do it.
    ///
    /// A wrapper still held in a local when <see cref="Release"/> is called is not collected in a Debug build, where a
    /// local lives to the end of its method; the tests are built and run in Release.
    /// </summary>
    internal static class Dao
    {
        /// <summary>The user's Access workgroup file, which Access makes for a user the first time it runs.</summary>
        public static readonly string Workgroup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Access", "System.mdw");

        private static readonly object Gate = new object();
        private static object _engine;

        /// <summary>
        /// The process's DAO engine - ACE's (DAO.DBEngine.120) where it is installed, else Jet 4.0's (DAO.DBEngine.36) -
        /// made the first time it is asked for and kept until the process ends. Never release it: a COM release of it
        /// would leave every later test a wrapper with nothing behind it.
        ///
        /// It is given the user's workgroup file where there is one. Access keeps its system tables from a reader until
        /// it is given leave to read them, which takes a workgroup file, and the engine takes the file only as it
        /// starts - in a 32-bit process it is shared with ACE's OLE DB provider, so an engine started without it keeps
        /// the system tables from every later reader in the process.
        /// </summary>
        public static object Engine
        {
            get
            {
                lock (Gate)
                {
                    if (_engine == null)
                    {
                        var dbeType = Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: false) ?? Type.GetTypeFromProgID("DAO.DBEngine.36", throwOnError: true);
                        var dbe = Activator.CreateInstance(dbeType);
                        if (File.Exists(Workgroup))
                        {
                            dbeType.InvokeMember("SystemDB", BindingFlags.SetProperty, null, dbe, new object[] { Workgroup });
                            dbeType.InvokeMember("DefaultUser", BindingFlags.SetProperty, null, dbe, new object[] { "Admin" });
                            dbeType.InvokeMember("DefaultPassword", BindingFlags.SetProperty, null, dbe, new object[] { "" });
                        }
                        _engine = dbe;
                    }
                    return _engine;
                }
            }
        }

        /// <summary>
        /// Lets go of what a test got from the engine - databases, recordsets, table definitions, fields - that it no
        /// longer holds: .NET lets go of each only when it collects. The engine itself is kept (<see cref="Engine"/>);
        /// <paramref name="engine"/> is taken only so that a call reads as what it ends.
        /// </summary>
        public static void Release(object engine)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
