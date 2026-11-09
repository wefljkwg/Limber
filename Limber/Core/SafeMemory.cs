using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Limber.Core;

/// <summary>
/// Reads game memory that may be freed while it is read (a character still loading or leaving: its models and
/// resources are made and thrown away on the game's other threads). Reading through a pointer into freed memory
/// crashes the game; these copy it with ReadProcessMemory instead, which just fails. Any thread.
/// For memory Limber owns, or that the game can't free meanwhile, plain pointer reads are fine (and faster).
/// </summary>
public static unsafe class SafeMemory
{
    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(nint process, nint address, void* buffer, nint size, out nint read);

    // (the current process's pseudo-handle)
    private const nint Self = -1;

    /// <summary>Could be an address in the game's own memory (not null, not a small number, not kernel space).</summary>
    public static bool Plausible(nint address) => address >= 0x10000 && address <= 0x7FFFFFFF0000;

    /// <summary>Copies <paramref name="length"/> bytes; false (nothing usable) if any of it couldn't be read.</summary>
    public static bool Copy(nint address, void* to, int length)
    {
        if(length <= 0)
            return length == 0;
        if(!Plausible(address))
            return false;
        return ReadProcessMemory(Self, address, to, length, out var read) && read == length;
    }

    /// <summary>Copies into part of an array; false if any of it couldn't be read.</summary>
    public static bool Copy(nint address, byte[] to, int offset, int length)
    {
        if(offset < 0 || length < 0 || offset + length > to.Length)
            return false;
        fixed(byte* p = to)
            return Copy(address, p + offset, length);
    }

    /// <summary>Reads one value (a pointer, a number, a whole struct).</summary>
    public static bool TryRead<T>(nint address, out T value) where T : unmanaged
    {
        T v = default;
        if(Copy(address, &v, sizeof(T)))
        {
            value = v;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Reads one value, or <paramref name="fallback"/> if it couldn't be read.</summary>
    public static T Read<T>(nint address, T fallback = default) where T : unmanaged
        => TryRead<T>(address, out var v) ? v : fallback;

    /// <summary>A pointer that also looks like one (8-byte aligned, in the game's memory); 0 if not.</summary>
    public static nint ReadPointer(nint address)
    {
        var p = Read<nint>(address);
        return Plausible(p) && (p & 7) == 0 ? p : 0;
    }

    /// <summary>A zero-ended UTF-8 string (at most <paramref name="maxLength"/> bytes); "" if it couldn't be read.</summary>
    public static string ReadString(nint address, int maxLength = 1024)
    {
        if(!Plausible(address) || maxLength <= 0)
            return "";
        var bytes = new byte[Math.Min(maxLength, 4096)];
        int have = 0;
        while(have < bytes.Length)
        {
            // (a piece at a time, never across a 4 KB page: the text may end just before memory that can't be read)
            nint at = address + have;
            int toPageEnd = 0x1000 - (int)(at & 0xFFF);
            int piece = Math.Min(Math.Min(64, toPageEnd), bytes.Length - have);
            if(!Copy(at, bytes, have, piece))
                break;
            int end = Array.IndexOf(bytes, (byte)0, have, piece);
            if(end >= 0)
                return Encoding.UTF8.GetString(bytes, 0, end);
            have += piece;
        }
        // (no end found in what could be read: not a string, or longer than allowed)
        return "";
    }

    /// <summary>
    /// An MSVC std::string (the game's resource paths): its text inline when shorter than 16 bytes, otherwise behind a
    /// pointer. "" if it's empty, longer than <paramref name="maxLength"/>, or couldn't be read.
    /// </summary>
    public static string ReadStdString(nint at, int maxLength = 1024)
    {
        ulong* head = stackalloc ulong[4];
        if(!Copy(at, head, 32))
            return "";
        ulong length = head[2], capacity = head[3];
        if(length == 0 || length > (ulong)maxLength)
            return "";
        var bytes = new byte[length];
        if(!Copy(capacity >= 16 ? (nint)head[0] : at, bytes, 0, (int)length))
            return "";
        return Encoding.UTF8.GetString(bytes);
    }
}
