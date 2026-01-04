using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace NvgSharp
{
	internal struct Path
	{
		public bool Closed = false;
		public int BevelCount = 0;
		public int FillOffset = 0, FillCount = 0;
		public int StrokeOffset = 0, StrokeCount = 0;
		public Winding Winding = (Winding)0;
		public bool Convex = false;
		public readonly List<NvgPoint> Points = new List<NvgPoint>();

		public Path()
		{
		}

		public NvgPoint this[int index]
		{
			readonly get => Points[index];
			set => Points[index] = value;
		}

		public readonly int Count => Points.Count;

		public readonly ref NvgPoint FirstPoint => ref CollectionsMarshal.AsSpan(Points)[0];
		public readonly ref NvgPoint LastPoint => ref CollectionsMarshal.AsSpan(Points)[^1];
	}
}
