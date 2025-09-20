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
	}
}