namespace _1Rad.Infrastructure.Services;

/// <summary>
/// Pure, IO-free volume reslicing for server-side MPR. Given an axial volume as
/// a flat <c>short[]</c> plus its patient-space geometry, it produces CORONAL or
/// SAGITTAL planes with the correct per-plane DICOM geometry (ImageOrientation/
/// Position/PixelSpacing). No fo-dicom, no blob IO — so the geometry math is unit
/// testable against synthetic volumes (the only way to verify reformat
/// correctness without a live DICOM pipeline; a geometry bug here = wrong
/// anatomy, so it must be tested, not eyeballed).
///
/// Voxel layout is z-major: <c>Voxels[z*(W*H) + y*W + x]</c> — x = column index
/// (fastest), y = row index, z = slice index. This matches how the extraction
/// pass writes each decoded+downsampled axial slice into plane <c>z = i</c>.
/// </summary>
public static class VolumeReformatter
{
    public enum Plane { Coronal, Sagittal }

    /// <summary>The accumulated axial volume + its patient-space geometry.</summary>
    public sealed class AxialVolume
    {
        public required short[] Voxels { get; init; }
        public required int Width  { get; init; }  // columns (x)
        public required int Height { get; init; }  // rows (y)
        public required int Depth  { get; init; }  // slices (z)
        // Unit direction cosines (patient space).
        public required double[] RowDir   { get; init; } // +x (DICOM IOP[0..2])
        public required double[] ColDir   { get; init; } // +y (DICOM IOP[3..5])
        public required double[] SliceDir { get; init; } // +z (slice normal)
        // Spacings in mm.
        public required double ColSpacing   { get; init; } // Δx
        public required double RowSpacing   { get; init; } // Δy
        public required double SliceSpacing { get; init; } // Δz
        public required double[] Origin { get; init; } // IPP of voxel (0,0,0)
    }

    /// <summary>One reformatted plane: pixels + the DICOM geometry tags it needs.</summary>
    public readonly record struct ReformatPlane(
        short[] Pixels,
        int Width,
        int Height,
        double[] ImageOrientationPatient, // 6 values
        double[] ImagePositionPatient,    // 3 values
        double RowSpacing,                // PixelSpacing[0]
        double ColSpacing,                // PixelSpacing[1]
        int PlaneIndex);                  // ordinal in the reformatted series

    /// <summary>
    /// Reslice the volume into planes of the requested orientation, lazily (one
    /// plane held at a time so the caller can encode+upload+release — bounded
    /// memory). <paramref name="maxPlanes"/> caps how many planes are emitted by
    /// decimating the plane axis (keeps encode/storage in check); intra-plane
    /// resolution is never reduced here.
    /// </summary>
    public static IEnumerable<ReformatPlane> Reslice(AxialVolume v, Plane plane, int maxPlanes)
    {
        int W = v.Width, H = v.Height, D = v.Depth;
        if (W <= 0 || H <= 0 || D <= 0 || v.Voxels.Length < (long)W * H * D)
            yield break;

        // Decimate the plane axis so we emit at most maxPlanes planes. Step ≥ 1.
        int planeAxisLen = plane == Plane.Coronal ? H : W;
        int step = maxPlanes > 0 ? Math.Max(1, (int)Math.Ceiling(planeAxisLen / (double)maxPlanes)) : 1;

        if (plane == Plane.Coronal)
        {
            // Coronal plane at fixed y: image is W(x) wide × D(z) tall.
            //   pixel(row=z, col=x) = vol[z*(W*H) + y*W + x]
            // Row axis = +x (RowDir), col axis = +z (SliceDir).
            // PixelSpacing = [Δz (between rows), Δx (between cols)].
            var iop = Concat(v.RowDir, v.SliceDir);
            int outIdx = 0;
            for (int y = 0; y < H; y += step)
            {
                var px = new short[W * D];
                for (int z = 0; z < D; z++)
                {
                    // Source row of W contiguous voxels → contiguous output row.
                    System.Array.Copy(v.Voxels, (long)z * W * H + (long)y * W, px, (long)z * W, W);
                }
                var ipp = Add(v.Origin, Scale(v.ColDir, y * v.RowSpacing));
                yield return new ReformatPlane(
                    px, W, D, iop, ipp,
                    RowSpacing: v.SliceSpacing, ColSpacing: v.ColSpacing, outIdx++);
            }
        }
        else
        {
            // Sagittal plane at fixed x: image is H(y) wide × D(z) tall.
            //   pixel(row=z, col=y) = vol[z*(W*H) + y*W + x]
            // Row axis = +y (ColDir), col axis = +z (SliceDir).
            // PixelSpacing = [Δz (between rows), Δy (between cols)].
            var iop = Concat(v.ColDir, v.SliceDir);
            int outIdx = 0;
            for (int x = 0; x < W; x += step)
            {
                var px = new short[H * D];
                for (int z = 0; z < D; z++)
                {
                    long baseSrc = (long)z * W * H + x;
                    int baseDst = z * H;
                    for (int y = 0; y < H; y++)
                        px[baseDst + y] = v.Voxels[baseSrc + (long)y * W];
                }
                var ipp = Add(v.Origin, Scale(v.RowDir, x * v.ColSpacing));
                yield return new ReformatPlane(
                    px, H, D, iop, ipp,
                    RowSpacing: v.SliceSpacing, ColSpacing: v.RowSpacing, outIdx++);
            }
        }
    }

    // ── tiny vector helpers ──────────────────────────────────────────────────
    public static double[] Cross(double[] a, double[] b) => new[]
    {
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    };

    public static double[] Normalize(double[] a)
    {
        double n = System.Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
        return n < 1e-9 ? new[] { 0.0, 0.0, 0.0 } : new[] { a[0] / n, a[1] / n, a[2] / n };
    }

    private static double[] Concat(double[] a, double[] b) => new[] { a[0], a[1], a[2], b[0], b[1], b[2] };
    private static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
    private static double[] Scale(double[] a, double s) => new[] { a[0] * s, a[1] * s, a[2] * s };
}
