namespace _1Rad.Domain.Constants;

/// <summary>
/// Centralized error codes for the application
/// </summary>
public static class ErrorCodes
{
    // Authentication & Authorization (AUTH_*)
    public const string AUTH_USER_NOT_FOUND = "AUTH_USER_NOT_FOUND";
    public const string AUTH_INVALID_CREDENTIALS = "AUTH_INVALID_CREDENTIALS";
    public const string AUTH_ACCOUNT_INACTIVE = "AUTH_ACCOUNT_INACTIVE";
    public const string AUTH_ACCOUNT_DEACTIVATED = "AUTH_ACCOUNT_DEACTIVATED";
    public const string AUTH_ACCOUNT_PENDING = "AUTH_ACCOUNT_PENDING";
    public const string AUTH_TOKEN_EXPIRED = "AUTH_TOKEN_EXPIRED";
    public const string AUTH_TOKEN_INVALID = "AUTH_TOKEN_INVALID";
    public const string AUTH_REFRESH_TOKEN_INVALID = "AUTH_REFRESH_TOKEN_INVALID";
    public const string AUTH_INSUFFICIENT_PERMISSIONS = "AUTH_INSUFFICIENT_PERMISSIONS";
    public const string AUTH_NO_HOSPITAL_ACCESS = "AUTH_NO_HOSPITAL_ACCESS";
    public const string AUTH_HOSPITAL_NOT_AUTHORIZED = "AUTH_HOSPITAL_NOT_AUTHORIZED";

    // OTP & Verification (OTP_*)
    public const string OTP_INVALID = "OTP_INVALID";
    public const string OTP_EXPIRED = "OTP_EXPIRED";
    public const string OTP_ALREADY_USED = "OTP_ALREADY_USED";
    public const string OTP_NOT_FOUND = "OTP_NOT_FOUND";
    public const string OTP_SEND_FAILED = "OTP_SEND_FAILED";
    public const string OTP_RATE_LIMIT_EXCEEDED = "OTP_RATE_LIMIT_EXCEEDED";

    // User Management (USER_*)
    public const string USER_NOT_FOUND = "USER_NOT_FOUND";
    public const string USER_ALREADY_EXISTS = "USER_ALREADY_EXISTS";
    public const string USER_EMAIL_EXISTS = "USER_EMAIL_EXISTS";
    public const string USER_MOBILE_EXISTS = "USER_MOBILE_EXISTS";
    public const string USER_INVALID_STATUS = "USER_INVALID_STATUS";
    public const string USER_NO_MAPPINGS = "USER_NO_MAPPINGS";

    // Hospital Management (HOSPITAL_*)
    public const string HOSPITAL_NOT_FOUND = "HOSPITAL_NOT_FOUND";
    public const string HOSPITAL_ALREADY_EXISTS = "HOSPITAL_ALREADY_EXISTS";
    public const string HOSPITAL_INACTIVE = "HOSPITAL_INACTIVE";
    public const string HOSPITAL_ACCESS_DENIED = "HOSPITAL_ACCESS_DENIED";

    // Personnel Management (PERSONNEL_*)
    public const string PERSONNEL_NOT_FOUND = "PERSONNEL_NOT_FOUND";
    public const string PERSONNEL_ALREADY_REGISTERED = "PERSONNEL_ALREADY_REGISTERED";
    public const string PERSONNEL_INVALID_ROLE = "PERSONNEL_INVALID_ROLE";
    public const string PERSONNEL_NO_ROLES = "PERSONNEL_NO_ROLES";
    public const string PERSONNEL_MAPPING_NOT_FOUND = "PERSONNEL_MAPPING_NOT_FOUND";

    // Role Management (ROLE_*)
    public const string ROLE_NOT_FOUND = "ROLE_NOT_FOUND";
    public const string ROLE_INVALID = "ROLE_INVALID";

    // Patient Management (PATIENT_*)
    public const string PATIENT_NOT_FOUND = "PATIENT_NOT_FOUND";
    public const string PATIENT_ALREADY_EXISTS = "PATIENT_ALREADY_EXISTS";
    public const string PATIENT_INVALID_MRN = "PATIENT_INVALID_MRN";

    // Appointment Management (APPOINTMENT_*)
    public const string APPOINTMENT_NOT_FOUND = "APPOINTMENT_NOT_FOUND";
    public const string APPOINTMENT_CONFLICT = "APPOINTMENT_CONFLICT";
    public const string APPOINTMENT_INVALID_TIME = "APPOINTMENT_INVALID_TIME";
    public const string APPOINTMENT_PAST_DATE = "APPOINTMENT_PAST_DATE";

    // Validation (VALIDATION_*)
    public const string VALIDATION_ERROR = "VALIDATION_ERROR";
    public const string VALIDATION_REQUIRED_FIELD = "VALIDATION_REQUIRED_FIELD";
    public const string VALIDATION_INVALID_FORMAT = "VALIDATION_INVALID_FORMAT";
    public const string VALIDATION_OUT_OF_RANGE = "VALIDATION_OUT_OF_RANGE";

    // External Services (SERVICE_*)
    public const string SERVICE_SMS_FAILED = "SERVICE_SMS_FAILED";
    public const string SERVICE_EMAIL_FAILED = "SERVICE_EMAIL_FAILED";
    public const string SERVICE_WHATSAPP_FAILED = "SERVICE_WHATSAPP_FAILED";
    public const string SERVICE_UNAVAILABLE = "SERVICE_UNAVAILABLE";

    // System Errors (SYSTEM_*)
    public const string SYSTEM_ERROR = "SYSTEM_ERROR";
    public const string SYSTEM_DATABASE_ERROR = "SYSTEM_DATABASE_ERROR";
    public const string SYSTEM_CONFIGURATION_ERROR = "SYSTEM_CONFIGURATION_ERROR";

    // General (GENERAL_*)
    public const string RESOURCE_NOT_FOUND = "RESOURCE_NOT_FOUND";
    public const string CONFLICT = "CONFLICT";
    public const string FORBIDDEN = "FORBIDDEN";
    public const string UNAUTHORIZED = "UNAUTHORIZED";
    public const string BAD_REQUEST = "BAD_REQUEST";
}
