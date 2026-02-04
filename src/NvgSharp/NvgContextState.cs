using System.Runtime.InteropServices;

namespace NvgSharp
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct NvgContextState
	{
		public int ShapeAntiAlias = 0;
		public Paint Fill = default;
		public Paint Stroke = default;
		public float StrokeWidth = 0;
		public float MiterLimit = 0;
		public LineCap LineJoin = LineCap.Butt;
		public LineCap LineCap = LineCap.Butt;
		public float Alpha = 0;
		public Transform Transform = new Transform();
		public Scissor Scissor = default;

		public NvgContextState()
		{
		}

		public readonly NvgContextState Clone()
		{
			return new NvgContextState
			{
				ShapeAntiAlias = ShapeAntiAlias,
				Fill = Fill,
				Stroke = Stroke,
				StrokeWidth = StrokeWidth,
				MiterLimit = MiterLimit,
				LineJoin = LineJoin,
				LineCap = LineCap,
				Alpha = Alpha,
				Transform = Transform,
				Scissor = Scissor,
			};
		}
	}
}
