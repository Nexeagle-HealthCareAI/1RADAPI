using System.Text.Json;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;
using FellowOakDicom.Serialization;

namespace _1Rad.Infrastructure.Interop;

/// <summary>
/// The second STOW-RS form (PS3.18 §10.5.1.2): a <c>application/dicom+json</c>
/// part holding an array of datasets whose binary values point at other parts by
/// <c>BulkDataURI</c> (matched against each part's <c>Content-Location</c>).
/// Each dataset is rebuilt into a DICOM Part 10 file and then stored exactly like
/// an <c>application/dicom</c> upload.
///
/// Pixel data in a bulk part labelled with a compressed transfer syntax
/// (e.g. <c>image/jpeg; transfer-syntax=1.2.840.10008.1.2.4.50</c>) is stored
/// encapsulated, one part = one frame; otherwise it is treated as uncompressed
/// Explicit VR Little Endian.
/// </summary>
public static class DicomJsonStowAssembler
{
    public sealed record BulkPart(string? Location, string? ContentType, byte[] Bytes);

    public sealed record Result(List<byte[]> Files, int Failed, List<string> Errors);

    public static Result Assemble(IReadOnlyList<string> jsonParts, IReadOnlyList<BulkPart> bulk)
    {
        DicomCodecs.Ensure();
        var files = new List<byte[]>();
        var errors = new List<string>();
        var failed = 0;

        foreach (var json in jsonParts)
        {
            List<string> items;
            try
            {
                using var doc = JsonDocument.Parse(json);
                items = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray().Select(e => e.GetRawText()).ToList()
                    : new List<string> { doc.RootElement.GetRawText() };
            }
            catch (JsonException ex)
            {
                failed++;
                errors.Add($"Unreadable DICOM JSON: {ex.Message}");
                continue;
            }

            foreach (var itemJson in items)
            {
                try
                {
                    files.Add(BuildFile(itemJson, bulk));
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add(ex.Message);
                }
            }
        }
        return new Result(files, failed, errors);
    }

    private static byte[] BuildFile(string itemJson, IReadOnlyList<BulkPart> bulk)
    {
        var parsed = DicomJson.ConvertJsonToDicom(itemJson, autoValidate: false);

        // Fill every BulkDataURI value from the matching part.
        BulkPart? pixelPart = null;
        foreach (var item in parsed.ToList())
        {
            if (item is not DicomElement el || el.Buffer is not BulkDataUriByteBuffer buffer) continue;
            var part = Find(bulk, buffer.BulkDataUri)
                       ?? throw new InvalidOperationException($"No part with Content-Location '{buffer.BulkDataUri}'.");
            if (el.Tag == DicomTag.PixelData) { pixelPart = part; continue; }
            buffer.Data = part.Bytes;
        }

        var syntax = TransferSyntaxOf(parsed, pixelPart);
        DicomDataset ds;
        if (pixelPart != null && syntax.IsEncapsulated)
        {
            // Compressed pixel data: rebuild in the compressed syntax, frame from the part.
            ds = new DicomDataset(syntax);
            foreach (var item in parsed)
                if (item.Tag != DicomTag.PixelData && item.Tag.Group != 0x0002) ds.Add(item);
            var pixel = DicomPixelData.Create(ds, true);
            pixel.AddFrame(new MemoryByteBuffer(pixelPart.Bytes));
        }
        else
        {
            ds = parsed;
            ds.Remove(t => t.Tag.Group == 0x0002); // file meta is regenerated
            if (pixelPart != null)
            {
                ds.Remove(DicomTag.PixelData);
                var bitsAllocated = ds.GetSingleValueOrDefault<ushort>(DicomTag.BitsAllocated, 16);
                if (bitsAllocated > 8) ds.AddOrUpdate(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(pixelPart.Bytes)));
                else ds.AddOrUpdate(new DicomOtherByte(DicomTag.PixelData, new MemoryByteBuffer(pixelPart.Bytes)));
            }
        }

        if (string.IsNullOrEmpty(ds.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty))
            || string.IsNullOrEmpty(ds.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty)))
            throw new InvalidOperationException("Dataset has no SOP Class / SOP Instance UID.");

        var file = new DicomFile(ds);
        using var ms = new MemoryStream();
        file.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Match a BulkDataURI to a part: exact Content-Location, else same path ending.</summary>
    public static BulkPart? Find(IReadOnlyList<BulkPart> parts, string uri)
    {
        var exact = parts.FirstOrDefault(p => string.Equals(p.Location?.Trim(), uri.Trim(), StringComparison.Ordinal));
        if (exact != null) return exact;
        var tail = uri.Trim().TrimEnd('/');
        return parts.FirstOrDefault(p => p.Location != null
            && (tail.EndsWith(p.Location.Trim().TrimEnd('/'), StringComparison.Ordinal)
                || p.Location.Trim().TrimEnd('/').EndsWith(tail, StringComparison.Ordinal)));
    }

    /// <summary>From the dataset's (0002,0010), else the pixel part's transfer-syntax parameter, else Explicit VR LE.</summary>
    private static DicomTransferSyntax TransferSyntaxOf(DicomDataset ds, BulkPart? pixelPart)
    {
        var uid = ds.GetSingleValueOrDefault(DicomTag.TransferSyntaxUID, string.Empty);
        if (string.IsNullOrEmpty(uid) && pixelPart?.ContentType != null)
            uid = TransferSyntaxParam(pixelPart.ContentType) ?? string.Empty;
        if (string.IsNullOrEmpty(uid)) return DicomTransferSyntax.ExplicitVRLittleEndian;
        try
        {
            return DicomTransferSyntax.Parse(uid);
        }
        catch
        {
            return DicomTransferSyntax.ExplicitVRLittleEndian;
        }
    }

    public static string? TransferSyntaxParam(string contentType)
    {
        foreach (var p in contentType.Split(';').Skip(1))
        {
            var kv = p.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("transfer-syntax", StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim().Trim('"');
        }
        return null;
    }
}
