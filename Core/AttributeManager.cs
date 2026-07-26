using System;
using System.Runtime.InteropServices;

namespace FemboyChanger.Core
{
    /// <summary>
    /// Source 2 CUtlVector head as it is laid out inside
    /// CUtlVectorEmbeddedNetworkVar&lt;CEconItemAttribute&gt;:
    ///   +0x00 int   m_Size
    ///   +0x04       (padding)
    ///   +0x08 T*    m_pElements
    ///   +0x10 int   m_nAllocationCount / flags
    /// Verified against CAttributeList::CAttributeList in client.dll (2026-07-22 build), which
    /// zeroes exactly a DWORD at list+0x08 and a QWORD at list+0x10.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct CUtlVectorHead
    {
        [FieldOffset(0)] public int Size;
        [FieldOffset(8)] public nint Elements;
    }

    /// <summary>
    /// CEconItemAttribute. Size and field offsets come from the schema registration in
    /// client.dll: <c>RegisterClass(..., "CEconItemAttribute", ..., size: 72, ...)</c> with
    /// m_iAttributeDefinitionIndex@0x30, m_flValue@0x34, m_flInitialValue@0x38,
    /// m_nRefundableCurrency@0x3C, m_bSetBonus@0x40.
    ///
    /// LayoutKind.Explicit with an exact Size is deliberate: the previous Sequential/Pack=1
    /// version marshalled its <c>bool</c> as a 4-byte Win32 BOOL, so Marshal.SizeOf returned 75
    /// instead of 72 and every attribute after the first landed at the wrong stride.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = CEconItemAttribute.Size)]
    public struct CEconItemAttribute
    {
        public const int Size = 72; // 0x48

        [FieldOffset(0x00)] public nint VTable;
        [FieldOffset(0x08)] public nint Owner;
        [FieldOffset(0x30)] public ushort DefIndex;
        [FieldOffset(0x34)] public float Value;
        [FieldOffset(0x38)] public float InitialValue;
        [FieldOffset(0x3C)] public int RefundableCurrency;
        [FieldOffset(0x40)] public byte SetBonus;
    }

    public static class AttributeManager
    {
        // econ attribute definition indices
        private const ushort ATTR_PAINT_KIT = 6;  // "set item texture prefab"
        private const ushort ATTR_SEED = 7;       // "set item texture seed"
        private const ushort ATTR_WEAR = 8;       // "set item texture wear"

        /// <summary>
        /// CUtlMemory "external const buffer" marker, stored in the allocation-count dword at
        /// vector+0x14. CAttributeList's destructor checks <c>(allocCount &amp; 0xC0000000) == 0</c>
        /// before handing m_pElements to g_pMemAlloc; setting these bits stops the game from
        /// trying to free pages that came from VirtualAllocEx and are not on its heap.
        /// </summary>
        private const uint ExternalConstBufferMarker = 0xC0000000;

        private const int AllocationCountOffset = 0x14;

        /// <summary>
        /// vtable pointer harvested from a real, game-owned CEconItemAttribute. Injected
        /// attributes reuse it so anything that virtual-dispatches on them stays valid; until one
        /// is seen we fall back to a null vtable (which is what the classic implementations do).
        /// </summary>
        private static nint _attributeVTable;

        /// <summary>
        /// Blocks we allocated in the game process. Only these are ever freed - a list the game
        /// owns must be left completely alone.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<nint> Owned = new();

        public static nint KnownVTable => _attributeVTable;

        /// <summary>Address of the CUtlVector head for a C_EconItemView.</summary>
        public static nint ListAddress(nint itemView) =>
            itemView + Offsets.m_AttributeList + Offsets.m_Attributes;

        /// <summary>
        /// Records the vtable of a game-owned attribute, if this item view happens to have one.
        /// Cheap enough to call on every weapon we walk past.
        /// </summary>
        public static void ObserveVTable(Memory mem, nint itemView)
        {
            if (_attributeVTable != 0) return;
            if (!mem.Read(ListAddress(itemView), out CUtlVectorHead head)) return;
            if (head.Size <= 0 || head.Size > 64 || head.Elements == 0) return;
            if (!mem.Read(head.Elements, out nint vtable) || vtable == 0) return;

            _attributeVTable = vtable;
            SkinChangerLogic.Log($"[Attr] Captured CEconItemAttribute vtable 0x{vtable:X} from a live item.");
        }

        /// <summary>
        /// Installs paint-kit / seed / wear attributes on an item view whose attribute list is
        /// currently empty. Returns the allocation so the caller can release it later.
        /// </summary>
        public static nint Create(Memory mem, nint itemView, SkinInfo skin)
        {
            if (skin.PaintKit <= 0) return 0;

            nint listAddr = ListAddress(itemView);
            if (!mem.Read(listAddr, out CUtlVectorHead existing))
            {
                SkinChangerLogic.Log($"[Attr] Could not read attribute list at 0x{listAddr:X}");
                return 0;
            }

            // Never stomp a list the game already owns - that would leak its allocation and can
            // crash when the game frees a block it no longer owns.
            if (existing.Size != 0 || existing.Elements != 0) return 0;

            CEconItemAttribute[] attributes =
            {
                MakeAttribute(ATTR_PAINT_KIT, skin.PaintKit),
                MakeAttribute(ATTR_SEED, skin.Seed),
                MakeAttribute(ATTR_WEAR, skin.Wear),
            };

            nint block = mem.Allocate(CEconItemAttribute.Size * attributes.Length);
            if (block == 0)
            {
                SkinChangerLogic.Log("[Attr] VirtualAllocEx for the attribute block failed.");
                return 0;
            }

            for (int i = 0; i < attributes.Length; i++)
                mem.Write(block + (i * CEconItemAttribute.Size), attributes[i]);

            // Publish the pointer before the count so the game can never observe a non-zero size
            // pointing at a stale/garbage array.
            mem.Write(listAddr + 8, block);
            mem.Write(listAddr + AllocationCountOffset, ExternalConstBufferMarker);
            mem.Write(listAddr, attributes.Length);
            lock (Owned) Owned.Add(block);
            return block;
        }

        /// <summary>
        /// Clears and frees an attribute list, but only if we are the ones who allocated it.
        /// Returns true if the list was ours and has been released.
        /// </summary>
        public static bool Remove(Memory mem, nint itemView)
        {
            nint listAddr = ListAddress(itemView);
            if (!mem.Read(listAddr, out CUtlVectorHead head)) return false;
            if (head.Elements == 0) return false;

            lock (Owned)
            {
                if (!Owned.Contains(head.Elements)) return false;
                Owned.Remove(head.Elements);
            }

            // Retire the count first, then the pointer, then release the pages.
            mem.Write(listAddr, 0);
            mem.Write(listAddr + 8, (nint)0);
            mem.Write(listAddr + AllocationCountOffset, 0u);
            mem.Free(head.Elements);
            return true;
        }

        /// <summary>Drops bookkeeping (used when the game process goes away).</summary>
        public static void Reset()
        {
            lock (Owned) Owned.Clear();
            _attributeVTable = 0;
        }

        private static CEconItemAttribute MakeAttribute(ushort defIndex, float value) => new()
        {
            VTable = _attributeVTable,
            Owner = 0,
            DefIndex = defIndex,
            Value = value,
            InitialValue = value,
            RefundableCurrency = 0,
            SetBonus = 0,
        };
    }
}
