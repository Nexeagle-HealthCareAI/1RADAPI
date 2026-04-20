namespace _1Rad.Domain.Constants;

/// <summary>
/// User-friendly error messages for the application
/// </summary>
public static class ErrorMessages
{
    // Authentication & Authorization
    public const string AUTH_USER_NOT_FOUND = "We couldn't find an account with those credentials. Please check and try again.";
    public const string AUTH_INVALID_CREDENTIALS = "The email/mobile or password you entered is incorrect. Please try again.";
    public const string AUTH_ACCOUNT_INACTIVE = "Your account is currently inactive. Please contact your administrator for assistance.";
    public const string AUTH_ACCOUNT_DEACTIVATED = "Your account has been deactivated. Please contact support to reactivate your account.";
    public const string AUTH_ACCOUNT_PENDING = "Your account is pending approval. You'll receive a notification once it's activated.";
    public const string AUTH_TOKEN_EXPIRED = "Your session has expired. Please log in again to continue.";
    public const string AUTH_TOKEN_INVALID = "Invalid authentication token. Please log in again.";
    public const string AUTH_REFRESH_TOKEN_INVALID = "Your session could not be refreshed. Please log in again.";
    public const string AUTH_INSUFFICIENT_PERMISSIONS = "You don't have permission to perform this action.";
    public const string AUTH_NO_HOSPITAL_ACCESS = "Your account is not associated with any hospital. Please contact your administrator.";
    public const string AUTH_HOSPITAL_NOT_AUTHORIZED = "You don't have access to this hospital. Please switch to an authorized hospital.";

    // OTP & Verification
    public const string OTP_INVALID = "The verification code you entered is incorrect. Please try again.";
    public const string OTP_EXPIRED = "This verification code has expired. Please request a new one.";
    public const string OTP_ALREADY_USED = "This verification code has already been used. Please request a new one.";
    public const string OTP_NOT_FOUND = "No active verification code found. Please request a new one.";
    public const string OTP_SEND_FAILED = "We couldn't send the verification code. Please try again in a few moments.";
    public const string OTP_RATE_LIMIT_EXCEEDED = "Too many verification attempts. Please wait a few minutes before trying again.";

    // User Management
    public const string USER_NOT_FOUND = "User not found. Please check the user ID and try again.";
    public const string USER_ALREADY_EXISTS = "An account with this email or mobile number already exists.";
    public const string USER_EMAIL_EXISTS = "This email address is already registered. Please use a different email.";
    public const string USER_MOBILE_EXISTS = "This mobile number is already registered. Please use a different number.";
    public const string USER_INVALID_STATUS = "Invalid user status. Please contact support.";
    public const string USER_NO_MAPPINGS = "This user is not associated with any hospital.";

    // Hospital Management
    public const string HOSPITAL_NOT_FOUND = "Hospital not found. Please verify the hospital ID.";
    public const string HOSPITAL_ALREADY_EXISTS = "A hospital with this name already exists in the system.";
    public const string HOSPITAL_INACTIVE = "This hospital is currently inactive.";
    public const string HOSPITAL_ACCESS_DENIED = "You don't have access to this hospital's data.";

    // Personnel Management
    public const string PERSONNEL_NOT_FOUND = "Staff member not found in this hospital.";
    public const string PERSONNEL_ALREADY_REGISTERED = "This staff member is already registered at this hospital.";
    public const string PERSONNEL_INVALID_ROLE = "One or more selected roles are invalid.";
    public const string PERSONNEL_NO_ROLES = "At least one role must be assigned to the staff member.";
    public const string PERSONNEL_MAPPING_NOT_FOUND = "Staff member mapping not found for this hospital.";

    // Role Management
    public const string ROLE_NOT_FOUND = "The specified role was not found.";
    public const string ROLE_INVALID = "Invalid role selection. Please choose from available roles.";

    // Patient Management
    public const string PATIENT_NOT_FOUND = "Patient not found. Please verify the patient ID or MRN.";
    public const string PATIENT_ALREADY_EXISTS = "A patient with this MRN already exists.";
    public const string PATIENT_INVALID_MRN = "Invalid Medical Record Number format.";

    // Appointment Management
    public const string APPOINTMENT_NOT_FOUND = "Appointment not found. Please verify the appointment ID.";
    public const string APPOINTMENT_CONFLICT = "This time slot is already booked. Please choose a different time.";
    public const string APPOINTMENT_INVALID_TIME = "Invalid appointment time. Please select a valid time slot.";
    public const string APPOINTMENT_PAST_DATE = "Cannot create appointments in the past. Please select a future date.";

    // Validation
    public const string VALIDATION_ERROR = "Please correct the errors and try again.";
    public const string VALIDATION_REQUIRED_FIELD = "This field is required.";
    public const string VALIDATION_INVALID_FORMAT = "Invalid format. Please check your input.";
    public const string VALIDATION_OUT_OF_RANGE = "Value is out of acceptable range.";

    // External Services
    public const string SERVICE_SMS_FAILED = "Failed to send SMS. Please try again or contact support.";
    public const string SERVICE_EMAIL_FAILED = "Failed to send email. Please try again or contact support.";
    public const string SERVICE_WHATSAPP_FAILED = "Failed to send WhatsApp message. Please try again or contact support.";
    public const string SERVICE_UNAVAILABLE = "This service is temporarily unavailable. Please try again later.";

    // System Errors
    public const string SYSTEM_ERROR = "An unexpected error occurred. Our team has been notified. Please try again later.";
    public const string SYSTEM_DATABASE_ERROR = "A database error occurred. Please try again or contact support if the problem persists.";
    public const string SYSTEM_CONFIGURATION_ERROR = "System configuration error. Please contact your administrator.";

    // General
    public const string RESOURCE_NOT_FOUND = "The requested resource was not found.";
    public const string CONFLICT = "This operation conflicts with existing data.";
    public const string FORBIDDEN = "You don't have permission to access this resource.";
    public const string UNAUTHORIZED = "Authentication is required to access this resource.";
    public const string BAD_REQUEST = "Invalid request. Please check your input and try again.";

    // Helper method to get message by error code
    public static string GetMessage(string errorCode)
    {
        return errorCode switch
        {
            ErrorCodes.AUTH_USER_NOT_FOUND => AUTH_USER_NOT_FOUND,
            ErrorCodes.AUTH_INVALID_CREDENTIALS => AUTH_INVALID_CREDENTIALS,
            ErrorCodes.AUTH_ACCOUNT_INACTIVE => AUTH_ACCOUNT_INACTIVE,
            ErrorCodes.AUTH_ACCOUNT_DEACTIVATED => AUTH_ACCOUNT_DEACTIVATED,
            ErrorCodes.AUTH_ACCOUNT_PENDING => AUTH_ACCOUNT_PENDING,
            ErrorCodes.AUTH_TOKEN_EXPIRED => AUTH_TOKEN_EXPIRED,
            ErrorCodes.AUTH_TOKEN_INVALID => AUTH_TOKEN_INVALID,
            ErrorCodes.AUTH_REFRESH_TOKEN_INVALID => AUTH_REFRESH_TOKEN_INVALID,
            ErrorCodes.AUTH_INSUFFICIENT_PERMISSIONS => AUTH_INSUFFICIENT_PERMISSIONS,
            ErrorCodes.AUTH_NO_HOSPITAL_ACCESS => AUTH_NO_HOSPITAL_ACCESS,
            ErrorCodes.AUTH_HOSPITAL_NOT_AUTHORIZED => AUTH_HOSPITAL_NOT_AUTHORIZED,
            ErrorCodes.OTP_INVALID => OTP_INVALID,
            ErrorCodes.OTP_EXPIRED => OTP_EXPIRED,
            ErrorCodes.OTP_ALREADY_USED => OTP_ALREADY_USED,
            ErrorCodes.OTP_NOT_FOUND => OTP_NOT_FOUND,
            ErrorCodes.OTP_SEND_FAILED => OTP_SEND_FAILED,
            ErrorCodes.OTP_RATE_LIMIT_EXCEEDED => OTP_RATE_LIMIT_EXCEEDED,
            ErrorCodes.USER_NOT_FOUND => USER_NOT_FOUND,
            ErrorCodes.USER_ALREADY_EXISTS => USER_ALREADY_EXISTS,
            ErrorCodes.USER_EMAIL_EXISTS => USER_EMAIL_EXISTS,
            ErrorCodes.USER_MOBILE_EXISTS => USER_MOBILE_EXISTS,
            ErrorCodes.HOSPITAL_NOT_FOUND => HOSPITAL_NOT_FOUND,
            ErrorCodes.PERSONNEL_NOT_FOUND => PERSONNEL_NOT_FOUND,
            ErrorCodes.PERSONNEL_ALREADY_REGISTERED => PERSONNEL_ALREADY_REGISTERED,
            ErrorCodes.PATIENT_NOT_FOUND => PATIENT_NOT_FOUND,
            ErrorCodes.APPOINTMENT_NOT_FOUND => APPOINTMENT_NOT_FOUND,
            ErrorCodes.SERVICE_SMS_FAILED => SERVICE_SMS_FAILED,
            ErrorCodes.SERVICE_EMAIL_FAILED => SERVICE_EMAIL_FAILED,
            ErrorCodes.SERVICE_WHATSAPP_FAILED => SERVICE_WHATSAPP_FAILED,
            ErrorCodes.SYSTEM_ERROR => SYSTEM_ERROR,
            _ => "An error occurred. Please try again."
        };
    }
}
