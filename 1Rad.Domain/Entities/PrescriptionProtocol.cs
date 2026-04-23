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
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation properties
        public User Doctor { get; set; }
        public Hospital Hospital { get; set; }
    }
}
