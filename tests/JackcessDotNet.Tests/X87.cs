using System.Runtime.InteropServices;

namespace JackcessDotNet.Tests;

/// <summary>
/// The precision a 32-bit .NET Framework process computes doubles with, on the x87 unit. A process starts at 53 bits.
/// ACE's OLE DB provider leaves the thread that ran a statement at 64 (measured with ACE's 32-bit provider, Microsoft
/// 365's and ACE 12: 53 bits before an INSERT, 64 after it), where a product is kept to 64 bits before it is rounded. Each method here runs its work on this thread at the precision it names and
/// then puts the unit back. A 64-bit process computes doubles with SSE2, which has no such state: there, and off
/// Windows, the work runs as it is.
/// </summary>
internal static class X87
{
    /// <summary>Whether this process computes doubles with the x87 unit, so that its precision can be set.</summary>
    public static bool Applies => !Environment.Is64BitProcess && RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary><paramref name="work"/>, with the unit at 64 bits of precision, as ACE leaves it.</summary>
    public static T At64Bits<T>(Func<T> work) => At(PrecisionControl64Bits, work);

    /// <summary><paramref name="work"/>, with the unit at 53 bits of precision, a process's own.</summary>
    public static T At53Bits<T>(Func<T> work) => At(PrecisionControl53Bits, work);

    /// <summary><paramref name="work"/>, with the unit at 64 bits of precision, as ACE leaves it.</summary>
    public static void At64Bits(Action work) => At(PrecisionControl64Bits, () => { work(); return 0; });

    private static T At<T>(uint precision, Func<T> work)
    {
        if (!Applies) return work();
        uint was = _controlfp(0, 0);
        _controlfp(precision, PrecisionControlMask);
        try { return work(); }
        finally { _controlfp(was, PrecisionControlMask); }
    }

    private const uint PrecisionControlMask = 0x00030000;   // _MCW_PC
    private const uint PrecisionControl64Bits = 0x00000000; // _PC_64
    private const uint PrecisionControl53Bits = 0x00010000; // _PC_53

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint _controlfp(uint newControl, uint mask);
}
