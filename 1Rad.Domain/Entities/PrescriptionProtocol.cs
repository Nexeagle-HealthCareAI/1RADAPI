using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities
{
    public class PrescriptionProtocol : IHospitalContext
    {
        public Guid Id { get; set; }
        public Guid DoctorId { get; set; }
        public Guid HospitalId { get; set; }
        public float HeaderMargin { get; set; }
        public float LeftMargin { get; set; }
        public float RightMargin { get; set; }
        public float BottomMargin { get; set; }
        public int FontSize { get; set; }
        public string FontColor { get; set; }
        public string FontFamily { get; set; }
        public string LetterheadBlobUrl { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation properties
        public User Doctor { get; set; }
        public Hospital Hospital { get; set; }
    }
}
