using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Imaging.NativeCodec;
using FellowOakDicom.IO.Buffer;
using FluentAssertions;

namespace _1Rad.UnitTests;

/// <summary>
/// Runtime proof for Step 2 of the DICOM performance roadmap: confirms the
/// fo-dicom.Codecs native encoder actually produces HTJ2K Lossless RPCL
/// (1.2.840.10008.1.2.4.202) — the syntax DicomExtractionService now emits —
/// and that it round-trips PIXEL-PERFECT (lossless). Mirrors the codec setup
/// the extraction service performs at construction.
/// </summary>
public class DicomHtj2kTranscodeTests
{
    static DicomHtj2kTranscodeTests()
    {
        new DicomSetupBuilder()
            .RegisterServices(s => s.AddTranscoderManager<NativeTranscoderManager>())
            .SkipValidation()
            .Build();
    }

    [Fact]
    public void Transcode_To_Htj2k_RPCL_RoundTrips_Losslessly()
    {
        // Arrange — a 64×64 16-bit (12-bit stored) MONOCHROME2 uncompressed slice
        // with a known pattern, all values < 4096 so the high bits are zero.
        const int w = 64, h = 64;
        var original = new byte[w * h * 2];
        for (int i = 0; i < w * h; i++)
        {
            ushort v = (ushort)((i * 7) % 4096);
            original[i * 2] = (byte)(v & 0xFF);
            original[i * 2 + 1] = (byte)(v >> 8);
        }

        var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);
        ds.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage);
        ds.AddOrUpdate(DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID());
        ds.AddOrUpdate(DicomTag.StudyInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID());
        ds.AddOrUpdate(DicomTag.SeriesInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID());
        ds.AddOrUpdate(DicomTag.Rows, (ushort)h);
        ds.AddOrUpdate(DicomTag.Columns, (ushort)w);
        ds.AddOrUpdate(DicomTag.BitsAllocated, (ushort)16);
        ds.AddOrUpdate(DicomTag.BitsStored, (ushort)12);
        ds.AddOrUpdate(DicomTag.HighBit, (ushort)11);
        ds.AddOrUpdate(DicomTag.PixelRepresentation, (ushort)0);
        ds.AddOrUpdate(DicomTag.SamplesPerPixel, (ushort)1);
        ds.AddOrUpdate(DicomTag.PhotometricInterpretation, "MONOCHROME2");

        var px = DicomPixelData.Create(ds, true);
        px.AddFrame(new MemoryByteBuffer(original));
        var srcFile = new DicomFile(ds);

        // Act — transcode to HTJ2K Lossless RPCL (exactly what extraction produces).
        var toHt = new DicomTranscoder(srcFile.Dataset.InternalTransferSyntax, DicomTransferSyntax.HTJ2KLosslessRPCL);
        var htFile = toHt.Transcode(srcFile);

        // Assert — really HTJ2K, and survives a Part-10 save/reload (valid file).
        htFile.Dataset.InternalTransferSyntax.Should().Be(DicomTransferSyntax.HTJ2KLosslessRPCL);
        using var ms = new MemoryStream();
        htFile.Save(ms);
        ms.Position = 0;
        var reloaded = DicomFile.Open(ms);
        reloaded.Dataset.InternalTransferSyntax.Should().Be(DicomTransferSyntax.HTJ2KLosslessRPCL);

        // Assert — decodes back to the EXACT original pixels (lossless).
        var back = new DicomTranscoder(DicomTransferSyntax.HTJ2KLosslessRPCL, DicomTransferSyntax.ExplicitVRLittleEndian)
            .Transcode(reloaded);
        var backBytes = DicomPixelData.Create(back.Dataset).GetFrame(0).Data;
        backBytes.Should().Equal(original);
    }

    /// <summary>
    /// Proves the EXACT pixel format that DicomExtractionService.BuildReformatDicomBytes
    /// emits for server-side coronal/sagittal — 16-bit SIGNED MONOCHROME2,
    /// BitsStored=16, with NEGATIVE values (HU-like) — survives HTJ2K Lossless RPCL
    /// pixel-perfectly. Closes a verification gap: the reformat path is otherwise
    /// only validatable against real DICOM, and the other test covers only unsigned
    /// 12-bit.
    /// </summary>
    [Fact]
    public void Transcode_Signed16_ReformatFormat_RoundTrips_Losslessly()
    {
        const int w = 48, h = 32;
        var original = new byte[w * h * 2];
        for (int i = 0; i < w * h; i++)
        {
            short v = (short)(((i * 37) % 4096) - 1024); // spans negative..positive like HU
            original[i * 2] = (byte)(v & 0xFF);
            original[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }

        var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);
        ds.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage);
        ds.AddOrUpdate(DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID());
        ds.AddOrUpdate(DicomTag.StudyInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID());
        ds.AddOrUpdate(DicomTag.SeriesInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID());
        ds.AddOrUpdate(DicomTag.Rows, (ushort)h);
        ds.AddOrUpdate(DicomTag.Columns, (ushort)w);
        ds.AddOrUpdate(DicomTag.BitsAllocated, (ushort)16);
        ds.AddOrUpdate(DicomTag.BitsStored, (ushort)16);
        ds.AddOrUpdate(DicomTag.HighBit, (ushort)15);
        ds.AddOrUpdate(DicomTag.PixelRepresentation, (ushort)1); // signed — the reformat format
        ds.AddOrUpdate(DicomTag.SamplesPerPixel, (ushort)1);
        ds.AddOrUpdate(DicomTag.PhotometricInterpretation, "MONOCHROME2");

        var px = DicomPixelData.Create(ds, true);
        px.AddFrame(new MemoryByteBuffer(original));
        var srcFile = new DicomFile(ds);

        var htFile = new DicomTranscoder(srcFile.Dataset.InternalTransferSyntax, DicomTransferSyntax.HTJ2KLosslessRPCL)
            .Transcode(srcFile);
        htFile.Dataset.InternalTransferSyntax.Should().Be(DicomTransferSyntax.HTJ2KLosslessRPCL);

        using var ms = new MemoryStream();
        htFile.Save(ms);
        ms.Position = 0;
        var reloaded = DicomFile.Open(ms);

        var back = new DicomTranscoder(DicomTransferSyntax.HTJ2KLosslessRPCL, DicomTransferSyntax.ExplicitVRLittleEndian)
            .Transcode(reloaded);
        var backBytes = DicomPixelData.Create(back.Dataset).GetFrame(0).Data;
        backBytes.Should().Equal(original);
    }
}
