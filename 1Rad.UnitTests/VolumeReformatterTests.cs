using _1Rad.Infrastructure.Services;
using FluentAssertions;

namespace _1Rad.UnitTests;

/// <summary>
/// Correctness proof for server-side MPR reslicing. A geometry bug here would
/// produce anatomically WRONG coronal/sagittal images (a patient-safety issue),
/// and it can't be eyeballed in a live pipeline — so the reslice math is pinned
/// here against a tiny synthetic volume whose every voxel is hand-verifiable.
///
/// Volume: W=4 (x), H=3 (y), D=2 (z). Each voxel encodes its coordinates as
/// value = x*100 + y*10 + z, so a reformatted pixel's value reveals exactly
/// which source voxel it came from. Canonical axial geometry (LPS-ish):
/// RowDir=+x, ColDir=+y, SliceDir=+z; spacings Δx=0.5, Δy=0.5, Δz=2.0;
/// origin (IPP of voxel 0,0,0) = (10,20,30).
/// </summary>
public class VolumeReformatterTests
{
    private const int W = 4, H = 3, D = 2;

    private static VolumeReformatter.AxialVolume MakeVolume()
    {
        var vox = new short[W * H * D];
        for (int z = 0; z < D; z++)
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    vox[z * (W * H) + y * W + x] = (short)(x * 100 + y * 10 + z);

        return new VolumeReformatter.AxialVolume
        {
            Voxels = vox, Width = W, Height = H, Depth = D,
            RowDir = new[] { 1.0, 0, 0 }, ColDir = new[] { 0.0, 1, 0 }, SliceDir = new[] { 0.0, 0, 1 },
            ColSpacing = 0.5, RowSpacing = 0.5, SliceSpacing = 2.0,
            Origin = new[] { 10.0, 20.0, 30.0 },
        };
    }

    [Fact]
    public void Coronal_Has_One_Plane_Per_Row_With_Correct_Pixels()
    {
        var planes = VolumeReformatter.Reslice(MakeVolume(), VolumeReformatter.Plane.Coronal, maxPlanes: 1000).ToList();

        planes.Should().HaveCount(H); // one coronal plane per axial row (y)
        // Each plane is W wide (x) × D tall (z).
        planes.Should().OnlyContain(p => p.Width == W && p.Height == D);

        // Plane y=0: pixel(row=z, col=x) = value(x, 0, z) = x*100 + z, laid out z-major.
        planes[0].Pixels.Should().Equal(new short[] { 0, 100, 200, 300, /* z=1 */ 1, 101, 201, 301 });
    }

    [Fact]
    public void Coronal_Geometry_Is_Correct()
    {
        var planes = VolumeReformatter.Reslice(MakeVolume(), VolumeReformatter.Plane.Coronal, 1000).ToList();

        // Row axis = +x, column axis = +z (the slice normal).
        planes[0].ImageOrientationPatient.Should().Equal(new[] { 1.0, 0, 0, 0, 0, 1 });
        // PixelSpacing = [between rows = Δz, between cols = Δx].
        planes[0].RowSpacing.Should().Be(2.0);
        planes[0].ColSpacing.Should().Be(0.5);
        // Each coronal plane steps along +y by Δy: IPP.y advances 0.5 per plane.
        planes[0].ImagePositionPatient.Should().Equal(new[] { 10.0, 20.0, 30.0 });
        planes[1].ImagePositionPatient.Should().Equal(new[] { 10.0, 20.5, 30.0 });
        planes[2].ImagePositionPatient.Should().Equal(new[] { 10.0, 21.0, 30.0 });
    }

    [Fact]
    public void Sagittal_Has_One_Plane_Per_Column_With_Correct_Pixels()
    {
        var planes = VolumeReformatter.Reslice(MakeVolume(), VolumeReformatter.Plane.Sagittal, 1000).ToList();

        planes.Should().HaveCount(W); // one sagittal plane per axial column (x)
        planes.Should().OnlyContain(p => p.Width == H && p.Height == D);

        // Plane x=0: pixel(row=z, col=y) = value(0, y, z) = y*10 + z, z-major.
        planes[0].Pixels.Should().Equal(new short[] { 0, 10, 20, /* z=1 */ 1, 11, 21 });
    }

    [Fact]
    public void Sagittal_Geometry_Is_Correct()
    {
        var planes = VolumeReformatter.Reslice(MakeVolume(), VolumeReformatter.Plane.Sagittal, 1000).ToList();

        // Row axis = +y, column axis = +z.
        planes[0].ImageOrientationPatient.Should().Equal(new[] { 0.0, 1, 0, 0, 0, 1 });
        planes[0].RowSpacing.Should().Be(2.0); // Δz
        planes[0].ColSpacing.Should().Be(0.5); // Δy
        // Each sagittal plane steps along +x by Δx: IPP.x advances 0.5 per plane.
        planes[0].ImagePositionPatient.Should().Equal(new[] { 10.0, 20.0, 30.0 });
        planes[1].ImagePositionPatient.Should().Equal(new[] { 10.5, 20.0, 30.0 });
    }

    [Fact]
    public void MaxPlanes_Decimates_The_Plane_Axis()
    {
        // H=3 coronal planes capped to 2 → step = ceil(3/2) = 2 → planes at y=0,2.
        var planes = VolumeReformatter.Reslice(MakeVolume(), VolumeReformatter.Plane.Coronal, maxPlanes: 2).ToList();

        planes.Should().HaveCount(2);
        planes[0].ImagePositionPatient.Should().Equal(new[] { 10.0, 20.0, 30.0 }); // y=0
        planes[1].ImagePositionPatient.Should().Equal(new[] { 10.0, 21.0, 30.0 }); // y=2 (skipped y=1)
    }

    [Fact]
    public void Cross_Product_Matches_Right_Hand_Rule()
    {
        VolumeReformatter.Cross(new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 })
            .Should().Equal(new[] { 0.0, 0, 1 });
    }
}
