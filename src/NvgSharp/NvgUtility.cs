using System;
using System.Runtime.CompilerServices;

#if MONOGAME || FNA
using Microsoft.Xna.Framework;
#elif STRIDE
using Stride.Core.Mathematics;
#elif MOONWORKS
using System.Numerics;
using Color = MoonWorks.Graphics.Color;
using Matrix = System.Numerics.Matrix4x4;
#else
using System.Drawing;
using System.Numerics;
using Matrix = System.Numerics.Matrix4x4;
#endif

namespace NvgSharp
{
	internal static class NvgUtility
	{
		/// <summary>
		/// Length proportional to radius of a cubic bezier handle for 90deg arcs
		/// </summary>
		public const float NVG_KAPPA90 = 0.5522847493f;

		/// <summary>
		/// PI
		/// </summary>
		public const float PI = MathF.PI;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float SqrtF(float a) => MathF.Sqrt(a);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float SinF(float a) => MathF.Sin(a);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float TanF(float a) => MathF.Tan(a);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float Atan2F(float a, float b) => MathF.Atan2(a, b);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float CosF(float a) => MathF.Cos(a);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float AcosF(float a) => MathF.Acos(a);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float CeilingF(float a) => MathF.Ceiling(a);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static int ClampI(int a, int mn, int mx)
		{
			if (a < mn) return a;
			if (a > mx) return mx;

			return a;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float ClampF(float a, float mn, float mx)
		{
			if (a < mn) return a;
			if (a > mx) return mx;

			return a;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float Cross(float dx0, float dy0, float dx1, float dy1) => dx1 * dy0 - dx0 * dy1;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static float Normalize(ref float x, ref float y)
		{
			var d = SqrtF((x * x) + (y * y));
			if (d > 1e-6f)
			{
				float id = 1.0f / d;
				x *= id;
				y *= id;
			}

			return d;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static void MakeZero(ref this Matrix m)
		{
			m.M11 = m.M12 = m.M13 = m.M14 = 0;
			m.M21 = m.M22 = m.M23 = m.M24 = 0;
			m.M31 = m.M32 = m.M33 = m.M34 = 0;
			m.M41 = m.M42 = m.M43 = m.M44 = 0;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static Color FromRGBA(byte r, byte g, byte b, byte a)
		{
#if MONOGAME || FNA || STRIDE || MOONWORKS
			return new Color(r, g, b, a);
#else
			return Color.FromArgb(a, r, g, b);
#endif
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static Vector4 ToVector4(this Color c, bool premultiply)
		{
			var result = new Vector4(c.R / 255.0f, c.G / 255.0f, c.B / 255.0f, c.A / 255.0f);

			if (premultiply)
			{
				result.X *= result.W;
				result.Y *= result.W;
				result.Z *= result.W;
			}

			return result;
		}
	}
}