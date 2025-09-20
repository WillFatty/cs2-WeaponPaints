namespace WeaponPaints
{
	public class WeaponInfo
	{
		public int Paint { get; set; }
		public int Seed { get; set; }
		public float Wear { get; set; }
		public string Nametag { get; set; } = "";
		public bool StatTrak { get; set; }
		public int StatTrakCount { get; set; }
		public KeyChainInfo? KeyChain { get; set; }
		public List<StickerInfo> Stickers { get; set; } = new();

		public void SetSticker(int slot, int id, int schema, float x, float y, float wear, float scale, float rotation)
		{
			// Ensure we have enough stickers
			while (Stickers.Count <= slot)
			{
				Stickers.Add(new StickerInfo());
			}

			Stickers[slot] = new StickerInfo
			{
				Id = (uint)id,
				Schema = (uint)schema,
				OffsetX = x,
				OffsetY = y,
				Wear = wear,
				Scale = scale,
				Rotation = rotation,
				Slot = slot // Set the slot value
			};
		}

		public void SetKeychain(int id, float x, float y, float z, int seed)
		{
			KeyChain = new KeyChainInfo
			{
				Id = (uint)id,
				OffsetX = x,
				OffsetY = y,
				OffsetZ = z,
				Seed = (uint)seed
			};
		}
	}

	public class StickerInfo
	{
		public uint Id { get; set; }
		public uint Schema { get; set; }
		public float OffsetX { get; set; }
		public float OffsetY { get; set; }
		public float Wear { get; set; }
		public float Scale { get; set; }
		public float Rotation { get; set; }
		public int Slot { get; set; } // Add slot property for proper positioning
	}

	public class KeyChainInfo
	{
		public uint Id { get; set; }
		public float OffsetX { get; set; }
		public float OffsetY { get; set; }
		public float OffsetZ { get; set; }
		public uint Seed { get; set; }
	}

	public class InspectWeaponData
	{
		public int WeaponDefIndex { get; set; }
		public int PaintId { get; set; }
		public float Wear { get; set; }
		public int Seed { get; set; }
		public int StatTrak { get; set; }
		public int StatTrakCount { get; set; }
		public string NameTag { get; set; } = string.Empty;
		public List<StickerInfo> Stickers { get; set; } = new();
		public KeyChainInfo Keychain { get; set; } = new();
	}
}