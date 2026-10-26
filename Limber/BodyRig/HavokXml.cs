using System;
using System.IO;
using System.Text;
using FFXIVClientStructs.Havok.Common.Base.System.IO.OStream;
using FFXIVClientStructs.Havok.Common.Base.Types;
using FFXIVClientStructs.Havok.Common.Serialize.Resource;
using FFXIVClientStructs.Havok.Common.Serialize.Util;

namespace Limber.BodyRig;

/// <summary>
/// Converts Havok data between the game's binary form and Havok's XML form, using the Havok code inside the
/// game itself (through FFXIVClientStructs), so Limber never has to write Havok's binary format by hand.
/// Game thread only. Uses temporary files, which are always deleted.
/// </summary>
public static unsafe class HavokXml
{
    /// <summary>Binary .hkx data to Havok XML.</summary>
    public static string FromBinary(byte[] hkx)
    {
        const hkSerializeUtil.SaveOptionBits options = hkSerializeUtil.SaveOptionBits.SerializeIgnoredMembers
            | hkSerializeUtil.SaveOptionBits.TextFormat
            | hkSerializeUtil.SaveOptionBits.WriteAttributes;
        var input = TempFile();
        try
        {
            File.WriteAllBytes(input, hkx);
            var resource = Load(input);
            if (resource == null)
                throw new InvalidDataException("The game's Havok couldn't read this skeleton.");
            var output = Save(resource, options);
            try { return File.ReadAllText(output); }
            finally { TryDelete(output); }
        }
        finally
        {
            TryDelete(input);
        }
    }

    /// <summary>Havok XML to binary .hkx data.</summary>
    public static byte[] ToBinary(string xml)
    {
        const hkSerializeUtil.SaveOptionBits options = hkSerializeUtil.SaveOptionBits.SerializeIgnoredMembers
            | hkSerializeUtil.SaveOptionBits.WriteAttributes;
        var input = TempFile();
        try
        {
            File.WriteAllText(input, xml);
            var resource = Load(input);
            if (resource == null)
                throw new InvalidDataException("The game's Havok couldn't read the changed skeleton.");
            var output = Save(resource, options);
            try { return File.ReadAllBytes(output); }
            finally { TryDelete(output); }
        }
        finally
        {
            TryDelete(input);
        }
    }

    private static hkResource* Load(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var registry = hkBuiltinTypeRegistry.Instance();
        var options = stackalloc hkSerializeUtil.LoadOptions[1];
        options->Flags = new hkFlags<hkSerializeUtil.LoadOptionBits, int> { Storage = (int)hkSerializeUtil.LoadOptionBits.Default };
        options->ClassNameRegistry = registry->GetClassNameRegistry();
        options->TypeInfoRegistry = registry->GetTypeInfoRegistry();
        return hkSerializeUtil.LoadFromFile(bytes, null, options);
    }

    private static string Save(hkResource* resource, hkSerializeUtil.SaveOptionBits bits)
    {
        var path = TempFile();
        var stream = new hkOstream();
        stream.Ctor(Encoding.UTF8.GetBytes(path));
        var result = stackalloc hkResult[1];
        var options = new hkSerializeUtil.SaveOptions
        {
            Flags = new hkFlags<hkSerializeUtil.SaveOptionBits, int> { Storage = (int)bits },
        };
        try
        {
            var registry = hkBuiltinTypeRegistry.Instance();
            const string name = "hkRootLevelContainer";
            var root = (hkRootLevelContainer*)resource->GetContentsPointer(name, registry->GetTypeInfoRegistry());
            if (root == null)
                throw new InvalidDataException("No Havok root container in this skeleton.");
            var type = registry->GetClassNameRegistry()->GetClassByName(name);
            if (type == null)
                throw new InvalidOperationException("The game's Havok type registry isn't available.");
            hkSerializeUtil.Save(result, root, type, stream.StreamWriter.ptr, options);
        }
        finally
        {
            stream.Dtor();
        }
        if (result->Result == hkResult.hkResultEnum.Failure)
        {
            TryDelete(path);
            throw new InvalidDataException("The game's Havok couldn't write the skeleton.");
        }
        return path;
    }

    private static string TempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Limber", "havok");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* temp file */ }
    }
}
