# Unit Test Coverage Plan for 1RadAPI

## Goal: Achieve 80%+ Test Coverage

## Test Status Legend
- ✅ Complete
- 🚧 In Progress
- ⏳ Pending

---

## 1. Finance Features

### Commands
- ✅ **CollectPayment** (11 tests)
  - Valid full payment
  - Valid partial payment
  - Empty hospital context
  - Zero/negative amount
  - Invoice not found
  - Different hospital invoice
  - Already paid invoice
  - Cancelled invoice
  - Payment exceeds balance
  - Payment properties validation
  - Multiple payments on same invoice

- 🚧 **GenerateInvoice** (10 tests planned)
  - Valid invoice with appointment
  - Valid invoice without appointment
  - Empty items list
  - Invalid item amount
  - Invalid item quantity
  - Patient not found
  - Patient from different hospital
  - Appointment not found
  - Appointment-patient mismatch
  - Invoice ID generation

- ⏳ **RecordExpense** (8 tests planned)
  - Valid expense
  - Empty hospital context
  - Invalid amount
  - Missing required fields
  - Category validation
  - Date validation

- ⏳ **UpsertServiceCharge** (8 tests planned)
  - Create new service charge
  - Update existing service charge
  - Invalid amount
  - Duplicate service name
  - Empty hospital context

- ⏳ **DeleteServiceCharge** (5 tests planned)
  - Valid deletion
  - Service charge not found
  - Different hospital
  - Empty hospital context

- ⏳ **SyncLocalStorageInvoices** (6 tests planned)
  - Valid sync
  - Empty invoices list
  - Invalid invoice data
  - Duplicate invoices

### Queries
- ⏳ **GetInvoices** (6 tests planned)
  - Get all invoices
  - Filter by status
  - Filter by date range
  - Search by patient name
  - Empty hospital context
  - Pagination

- ⏳ **GetFinanceStats** (5 tests planned)
  - Valid stats calculation
  - Empty data
  - Date range filter
  - Empty hospital context

- ⏳ **GetFinancialMatrix** (5 tests planned)
  - Valid matrix
  - Date range filter
  - Empty data
  - Empty hospital context

- ⏳ **GetPendingBillables** (5 tests planned)
  - Valid pending billables
  - Empty data
  - Filter by modality
  - Empty hospital context

- ⏳ **GetServiceCharges** (4 tests planned)
  - Get all service charges
  - Filter by modality
  - Empty hospital context

- ⏳ **GetExpenses** (6 tests planned)
  - Get all expenses
  - Filter by category
  - Filter by date range
  - Search
  - Empty hospital context

- ⏳ **ExportFinancials** (5 tests planned)
  - Export to Excel
  - Date range filter
  - Empty data
  - Empty hospital context

---

## 2. Appointments Features

### Commands
- ⏳ **CreateAppointment** (10 tests planned)
  - Valid appointment
  - Patient not found
  - Invalid date/time
  - Duplicate appointment
  - Empty hospital context
  - Required fields validation

- ⏳ **UpdateAppointmentStatus** (8 tests planned)
  - Valid status update
  - Invalid status
  - Appointment not found
  - Different hospital
  - Empty hospital context

- ⏳ **ImportAppointments** (6 tests planned)
  - Valid import
  - Empty list
  - Invalid data
  - Duplicate appointments

### Queries
- ⏳ **GetAppointments** (8 tests planned)
  - Get all appointments
  - Filter by status
  - Search by patient
  - Date range filter
  - Empty hospital context
  - Pagination

- ⏳ **GetAppointmentById** (5 tests planned)
  - Valid appointment
  - Not found
  - Different hospital
  - Empty hospital context

- ⏳ **GetStrategicOutlook** (6 tests planned)
  - Valid outlook
  - Date range filter
  - Empty data
  - Empty hospital context

---

## 3. Patients Features

### Commands
- ⏳ **CreatePatient** (10 tests planned)
  - Valid patient
  - Duplicate mobile
  - Invalid mobile format
  - Required fields validation
  - Empty hospital context

- ⏳ **UpdatePatient** (8 tests planned)
  - Valid update
  - Patient not found
  - Different hospital
  - Empty hospital context

- ⏳ **DeletePatient** (5 tests planned)
  - Valid deletion
  - Patient not found
  - Different hospital
  - Has active appointments

### Queries
- ⏳ **GetPatients** (8 tests planned)
  - Get all patients
  - Search by name/mobile
  - Filter by referrer
  - Pagination
  - Empty hospital context

- ⏳ **GetPatientById** (5 tests planned)
  - Valid patient
  - Not found
  - Different hospital
  - Empty hospital context

---

## 4. Referrers Features

### Commands
- ⏳ **CreateReferrer** (8 tests planned)
- ⏳ **UpdateReferrer** (6 tests planned)
- ⏳ **DeleteReferrer** (5 tests planned)

### Queries
- ⏳ **GetReferrers** (6 tests planned)
- ⏳ **GetReferralIntelligence** (5 tests planned)
- ⏳ **ExportReferralIntelligence** (4 tests planned)

---

## 5. Auth Features (Already Complete)
- ✅ DeployInfrastructure
- ✅ ForgotPassword
- ✅ IdentitySetup
- ✅ Login
- ✅ RefreshToken
- ✅ ResetPassword
- ✅ SendOTP
- ✅ SwitchContext
- ✅ VerifyOTP
- ✅ VerifyResetCode

---

## 6. Hospitals Features (Partially Complete)
- ✅ GetHospitalDetails
- ✅ UpdateHospitalDetails
- ⏳ **CreateChain** (8 tests planned)

---

## 7. Personnel Features (Already Complete)
- ✅ GetHospitalPersonnel
- ✅ RegisterStaff
- ✅ RemoveStaff
- ✅ UpdateStaff

---

## Test Coverage Summary

### Current Status
- **Total Features**: ~70
- **Completed Tests**: ~25 (35%)
- **Target**: 80%+

### Priority Order
1. ✅ Finance (Critical - just fixed bugs)
2. Appointments (High usage)
3. Patients (Core functionality)
4. Referrers (Business intelligence)
5. Hospitals (Configuration)

### Estimated Tests Needed
- Finance: 80 tests
- Appointments: 40 tests
- Patients: 40 tests
- Referrers: 30 tests
- Hospitals: 10 tests
- **Total**: ~200 tests for 80% coverage

---

## Testing Best Practices
1. Test happy path first
2. Test all validation rules
3. Test authorization (hospital context)
4. Test edge cases (null, empty, invalid)
5. Test error handling
6. Use descriptive test names
7. Follow AAA pattern (Arrange, Act, Assert)
8. Mock external dependencies
9. Test one thing per test
10. Keep tests independent
