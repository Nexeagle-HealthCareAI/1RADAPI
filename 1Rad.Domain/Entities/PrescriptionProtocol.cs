using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities
{
    public class PrescriptionProtocol : IHospitalContext
    {
        public Guid Id { get; set; }
        public Guid DoctorId { get; set; }
        public Guid HospitalId { get; set; }
        public decimal HeaderMargin { get; set; }
        public decimal LeftMargin { get; set; }
        public decimal RightMargin { get; set; }
        public decimal BottomMargin { get; set; }
        public int FontSize { get; set; }
        public string FontColor { get; set; }
        public string FontFamily { get; set; }
        public string? LetterheadBlobUrl { get; set; }
        public string? OverflowBackgroundMode { get; set; } // 'REUSE' or 'BLANK'

        // Report paper (migration 111): how a printed report is laid out.
        //   IMAGES     header + footer pictures, text in between
        //   STANDARD   built-in design from the centre's details + logo + colour
        //   LETTERHEAD pre-printed paper (or the full-page LetterheadBlobUrl);
        //              HeaderMargin / BottomMargin are kept empty
        public string PaperMode { get; set; } = PaperModes.Letterhead;
        public string? HeaderImageBlobUrl { get; set; }
        public string? FooterImageBlobUrl { get; set; }
        public string? LogoBlobUrl { get; set; }
        public string? AccentColor { get; set; }
        public bool ShowRegistration { get; set; } = true;
        public bool ShowQr { get; set; } = true;
        // LETTERHEAD only: give PDF / WhatsApp copies the standard header.
        public bool PdfStandardHeader { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation properties
        public User Doctor { get; set; }
        public Hospital Hospital { get; set; }
    }

    public static class PaperModes
    {
        public const string Images = "IMAGES";
        public const string Standard = "STANDARD";
        public const string Letterhead = "LETTERHEAD";

        public static bool IsValid(string? mode) =>
            mode == Images || mode == Standard || mode == Letterhead;
    }
}
