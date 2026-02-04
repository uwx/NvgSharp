namespace NvgSharp
{
	internal struct NvgPoint
	{
		public float X = 0;
		public float Y = 0;
		public float DeltaX = 0;
		public float DeltaY = 0;
		public float Length = 0;
		public float dmx = 0;
		public float dmy = 0;
		public byte flags = 0;

		public NvgPoint()
		{
		}
	}
}