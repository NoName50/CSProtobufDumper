// #define MapField
// #define OneOf

using System;
using System.Collections.Generic;

namespace CSProtobufDumper
{
    public class ProtoMessage
    {
        public string protoName = "";
        public List<Type> importTypeList = new List<Type>();
        public List<ProtoField> fieldList = new List<ProtoField>();
        #if OneOf
        public List<OneOf> oneOfList = new List<OneOf>();
        #endif
    }

    public class ProtoField
    {
        public string fieldName = "";
        public string fieldType = "";
        #if MapField
        public string mapKey = null;
        public string mapValue = null;
        #endif
        public bool isRepeated;
        public uint fieldNumber;
    }

    #if OneOf
    public class OneOf
    {
        public string oneOfName = "";
        public List<ProtoField> fieldList = new List<ProtoField>();
    }
    #endif

    public class ProtoEnum
    {
        public string enumName = "";
        public Dictionary<string, int> valDict = new Dictionary<string, int>();
    }

}