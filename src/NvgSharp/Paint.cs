using System.Runtime.InteropServices;

#if MONOGAME || FNA
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#elif STRIDE
using Stride.Core.Mathematics;
#else
using System.Numerics;
using System.Drawing;
using Texture2D = System.Object;
#endif

namespace NvgSharp
{
	[StructLayout(LayoutKind.Sequential)]
	public struct Paint(Color color)
	{
		public Transform Transform = Transform.Identity;
		public Vector2 Extent = new();
		public float Radius = 0.0f;
		public float Feather = 1.0f;
		public Color InnerColor = color;
		public Color OuterColor = color;
		public Texture2D Image = null;

		public void Zero()
		{
			Transform.Zero();
			Extent = Vector2.Zero;
			Radius = 0;
			Feather = 0;
			InnerColor = Color.Transparent;
			OuterColor = Color.Transparent;
			Image = null;
		}
	}
}