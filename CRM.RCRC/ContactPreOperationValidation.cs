using Microsoft.Xrm.Sdk;
using System;
using System.Text.RegularExpressions;

namespace RCRC.CRM.Plugins
{
    public class ContactPreOperationValidation : IPlugin
    {
        // =========================
        // Adjust these schema names
        // =========================
        private const string ENTITY_NAME = "contact";

        private const string FIELD_IDENTITY_TYPE = "crm2p_identitytype";     // OptionSet
        private const string FIELD_IDENTITY_NUMBER = "crm2p_identitynumber"; // Text
        private const string FIELD_BIRTHDATE = "birthdate";                  // Or your custom field
        private const string FIELD_MOBILE = "mobilephone";                   // Or your custom field
        private const string FIELD_EMAIL = "emailaddress1";                  // Or your custom field
        private const string FIELD_CITY = "crm2p_city";                      // Lookup
        private const string FIELD_DISTRICT = "crm2p_district";              // Lookup

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));

            if (context == null)
                return;

            if (!context.InputParameters.Contains("Target") || !(context.InputParameters["Target"] is Entity target))
                return;

            if (!string.Equals(target.LogicalName, ENTITY_NAME, StringComparison.OrdinalIgnoreCase))
                return;

            if (context.Stage != 20) // PreOperation
                return;

            if (context.MessageName != "Create" && context.MessageName != "Update")
                return;

            Entity preImage = null;
            if (context.PreEntityImages.Contains("PreImage"))
                preImage = context.PreEntityImages["PreImage"];

            ValidateContact(target, preImage);
        }

        private static void ValidateContact(Entity target, Entity preImage)
        {
            var identityType = GetOptionSet(target, preImage, FIELD_IDENTITY_TYPE);
            if (identityType == null)
            {
                ThrowValidation(
                    "[Data Validation] Identity Type is required.",
                    "[تحقق البيانات] نوع الهوية حقل إلزامي."
                );
            }
            
            var identityNumber = GetString(target, preImage, FIELD_IDENTITY_NUMBER);
            if (string.IsNullOrWhiteSpace(identityNumber))
            {
                ThrowValidation(
                    "[Data Validation] Identity Number is required.",
                    "[تحقق البيانات] رقم الهوية حقل إلزامي."
                );
            }

            if (!IsValidId(identityNumber, identityType.Value))
            {
                ThrowValidation(
                    "[Data Validation] Identity Number is invalid.",
                    "[تحقق البيانات] رقم الهوية خاطئ."
                );
            }

            var birthDate = GetDate(target, preImage, FIELD_BIRTHDATE);
            if (birthDate == null)
            {
                ThrowValidation(
                    "[Data Validation] Birth Date is required.",
                    "[تحقق البيانات] تاريخ الميلاد حقل إلزامي."
                );
            }

            if (birthDate != null)
            {
                int age = CalculateAge(birthDate.Value.Date, DateTime.UtcNow.Date);
                if (age < 18)
                {
                    ThrowValidation(
                        "[Data Validation] Age must be 18 or above.",
                        "[تحقق البيانات] يجب ألا يقل العمر عن 18 سنة."
                    );
                }
            }

            var mobile = GetString(target, preImage, FIELD_MOBILE);
            if (string.IsNullOrWhiteSpace(mobile))
            {
                ThrowValidation(
                    "[Data Validation] Mobile Number is required.",
                    "[تحقق البيانات] رقم الجوال حقل إلزامي."
                );
            }

            if (!string.IsNullOrWhiteSpace(mobile) && !IsValidSaudiMobile(mobile))
            {
                ThrowValidation(
                    "[Data Validation] Mobile phone needs to begin with 05, +9665 or 009665.",
                    "[تحقق البيانات] رقم الجوال يجب أن يبدأ بـ 05 أو +9665 أو 009665."
                );
            }

            var email = GetString(target, preImage, FIELD_EMAIL);
            if (!string.IsNullOrWhiteSpace(email) && !IsValidEmail(email))
            {
                ThrowValidation(
                    "[Data Validation] Email needs to follow the format mail@domain.com.",
                    "[تحقق البيانات] البريد الإلكتروني يجب أن يكون بالصيغة mail@domain.com."
                );
            }

            var city = GetLookup(target, preImage, FIELD_CITY);
            if (city == null)
            {
                ThrowValidation(
                    "[Data Validation] City is required.",
                    "[تحقق البيانات] المدينة حقل إلزامي."
                );
            }

            var district = GetLookup(target, preImage, FIELD_DISTRICT);
            if (district == null)
            {
                ThrowValidation(
                    "[Data Validation] District is required.",
                    "[تحقق البيانات] الحي حقل إلزامي."
                );
            }
        }

        private static bool IsValidId(string id, int idType)
        {
            id = id.Trim();

            switch (idType)
            {
                case 1: // National ID
                    return Regex.IsMatch(id.Trim(), @"^1\d{9}$");

                case 2: // Iqama
                    return Regex.IsMatch(id.Trim(), @"^2\d{9}$");

                case 3: // Passport
                    return Regex.IsMatch(id, @"^[a-zA-Z0-9]{5,20}$");

                case 4: // GCC ID
                    return Regex.IsMatch(id, @"^[a-zA-Z0-9]{5,15}$"); // basic safe rule

                default:
                    return false;
            }
        }

        private static bool IsValidSaudiMobile(string value)
        {
            value = value.Trim();
            return value.StartsWith("05") || value.StartsWith("+9665") || value.StartsWith("009665");
        }

        private static bool IsValidEmail(string email)
        {
            return Regex.IsMatch(
                email.Trim(),
                @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
                RegexOptions.IgnoreCase
            );
        }

        private static int CalculateAge(DateTime birthDate, DateTime today)
        {
            int age = today.Year - birthDate.Year;
            if (birthDate > today.AddYears(-age))
                age--;
            return age;
        }

        private static EntityReference GetLookup(Entity target, Entity preImage, string fieldName)
        {
            if (target.Contains(fieldName))
                return target.GetAttributeValue<EntityReference>(fieldName);

            return preImage?.GetAttributeValue<EntityReference>(fieldName);
        }

        private static string GetString(Entity target, Entity preImage, string fieldName)
        {
            if (target.Contains(fieldName))
                return target.GetAttributeValue<string>(fieldName);

            return preImage?.GetAttributeValue<string>(fieldName);
        }

        private static int? GetOptionSet(Entity target, Entity preImage, string fieldName)
        {
            if (target.Contains(fieldName))
                return target.GetAttributeValue<OptionSetValue>(fieldName)?.Value;

            return preImage?.GetAttributeValue<OptionSetValue>(fieldName)?.Value;
        }

        private static DateTime? GetDate(Entity target, Entity preImage, string fieldName)
        {
            if (target.Contains(fieldName))
                return target.GetAttributeValue<DateTime?>(fieldName);

            return preImage?.GetAttributeValue<DateTime?>(fieldName);
        }

        private static void ThrowValidation(string en, string ar)
        {
            throw new InvalidPluginExecutionException(
                $"{en}\n{ar}"
            );
        }
    }
}