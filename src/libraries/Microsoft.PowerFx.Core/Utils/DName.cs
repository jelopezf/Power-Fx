﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Text;

namespace Microsoft.PowerFx.Core.Utils
{
    // DName refers to a string that is valid as the name of a table/column.
    // That is any string that:
    // - does not consist entirely of space characters.
    public struct DName : ICheckable, IEquatable<DName>, IEquatable<string>
    {
        private const string StrUnderscore = "_";
        private const char ChSpace = ' ';
        private readonly string _value;

        public DName(string value)
        {
            Contracts.Assert(IsValidDName(value));
            _value = value;
        }

        public string Value { get { return _value ?? string.Empty; } }

        public bool IsValid { get { return _value != null; } }

        public static implicit operator string(DName name)
        {
            return name.Value;
        }

        public override string ToString()
        {
            return Value;
        }

        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            Contracts.AssertValueOrNull(obj);

            if (!(obj is DName))
                return false;

            return Equals((DName)obj);
        }

        public bool Equals(DName other)
        {
            return Value == other.Value;
        }

        public bool Equals(string other)
        {
            Contracts.AssertValueOrNull(other);
            return Value == other;
        }

        public static bool operator ==(DName name1, DName name2)
        {
            return name1.Value == name2.Value;
        }

        public static bool operator ==(string str, DName name)
        {
            Contracts.AssertValueOrNull(str);
            return str == name.Value;
        }

        public static bool operator ==(DName name, string str)
        {
            Contracts.AssertValueOrNull(str);
            return name.Value == str;
        }

        public static bool operator !=(DName name1, DName name2)
        {
            return name1.Value != name2.Value;
        }

        public static bool operator !=(string str, DName name)
        {
            Contracts.AssertValueOrNull(str);
            return str != name.Value;
        }

        public static bool operator !=(DName name, string str)
        {
            Contracts.AssertValueOrNull(str);
            return name.Value != str;
        }

        // Returns whether the given name is a valid DName as defined above.
        public static bool IsValidDName(string strName)
        {
            Contracts.AssertValueOrNull(strName);

            if (string.IsNullOrEmpty(strName))
                return false;

            for (int i = 0; i < strName.Length; i++)
            {
                char ch = strName[i];
                if (!char.IsWhiteSpace(ch))
                    return true;
            }

            return false;
        }

        // Takes a name and makes it into a valid DName
        // If the name contains all spaces, an underscore is prepended to the name.
        // Returns whether it had to be changed to be a valid DName in the fModified arg.
        public static DName MakeValid(string strName, out bool fModified)
        {
            Contracts.AssertValueOrNull(strName);

            if (string.IsNullOrEmpty(strName))
            {
                fModified = true;
                return new DName(StrUnderscore);
            }

            bool fAllSpaces = true;
            bool fHasSpecialWhiteSpaceCharacters = false;
            fModified = false;

            for (int i = 0; i < strName.Length; i++)
            {
                bool fIsSpace = strName[i] == ChSpace;
                bool fIsWhiteSpace = char.IsWhiteSpace(strName[i]);
                fAllSpaces = fAllSpaces && fIsWhiteSpace;
                fHasSpecialWhiteSpaceCharacters = fHasSpecialWhiteSpaceCharacters || (fIsWhiteSpace && !fIsSpace);
            }

            if (fHasSpecialWhiteSpaceCharacters)
            {
                fModified = true;
                StringBuilder builder = new StringBuilder(strName.Length);

                for (int i=0; i < strName.Length; i++)
                {
                    if(char.IsWhiteSpace(strName[i]))
                    {
                        builder.Append(ChSpace);
                    } else
                    {
                        builder.Append(strName[i]);
                    }
                }

                strName = builder.ToString();
            }

            if (!fAllSpaces)
                return new DName(strName);

            fModified = true;

            return new DName(StrUnderscore + strName);
        }
    }
}
