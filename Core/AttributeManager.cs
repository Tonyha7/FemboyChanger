using System;
using System.Runtime.InteropServices;

namespace FemboyChanger.Core
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CPtrGameVector
    {
        public ulong size;
        public nint ptr;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CEconItemAttribute
    {
        public nint vtable; // 0x0000
        public nint owner; // 0x0008
        public unsafe fixed byte pad_0010[32]; // 0x0010
        public ushort defIndex; // 0x0030
        public unsafe fixed byte pad_0032[2]; // 0x0032
        public float value; // 0x0034
        public float initValue; // 0x0038
        public int refundableCurrency; // 0x003C
        public bool setBonus; // 0x0040
        public unsafe fixed byte pad_0041[7]; // 0x0041
    }

    public static class AttributeManager
    {
        public static void Create(Memory mem, nint item, SkinInfo skin)
        {
            if (skin.PaintKit <= 0) return;

            CEconItemAttribute[] attributes = new CEconItemAttribute[3];
            attributes[0] = MakeAttribute(6, skin.PaintKit); // Paint
            attributes[1] = MakeAttribute(7, skin.Seed);     // Pattern
            attributes[2] = MakeAttribute(8, skin.Wear);     // Wear

            nint attributeListAddr = item + Offsets.m_AttributeList + Offsets.m_Attributes;
            CPtrGameVector preAttributes = mem.Read<CPtrGameVector>(attributeListAddr);

            if (preAttributes.size > 0 || preAttributes.ptr != 0) return; 

            int structSize = Marshal.SizeOf<CEconItemAttribute>();
            nint memBlock = mem.Allocate(structSize * attributes.Length);

            for (int i = 0; i < attributes.Length; i++)
            {
                mem.Write(memBlock + (i * structSize), attributes[i]);
            }

            CPtrGameVector newAttributes = new CPtrGameVector { size = (ulong)attributes.Length, ptr = memBlock };
            mem.Write(attributeListAddr, newAttributes);
        }

        public static void Remove(Memory mem, nint item)
        {
            nint attributeListAddr = item + Offsets.m_AttributeList + Offsets.m_Attributes;
            CPtrGameVector attributes = mem.Read<CPtrGameVector>(attributeListAddr);
            if (attributes.size == 0) return;

            mem.Write(attributeListAddr, new CPtrGameVector { size = 0, ptr = 0 });
            mem.Free(attributes.ptr);
        }

        private static CEconItemAttribute MakeAttribute(ushort def, float value)
        {
            unsafe
            {
                var attr = new CEconItemAttribute
                {
                    defIndex = def,
                    value = value,
                    initValue = value
                };
                return attr;
            }
        }
    }
}
