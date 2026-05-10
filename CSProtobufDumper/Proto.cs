using System.Collections.Generic;

namespace CSProtobufDumper
{
    public class ProtoMessage
    {
        public string protoName = "";
        public List<string> importNameList = new List<string>();
        public List<ProtoFIeld> fieldList = new List<ProtoFIeld>();
        public List<OneOf> oneOfList = new List<OneOf>();    
    }

    public class ProtoFIeld
    {
        public string fieldName = "";
        public string fieldType = "";
        public string mapKey = null;
        public string mapValue = null;
        public bool isRepeated;
        public uint val;
    }

    public class OneOf
    {
        public string oneOfName = "";
        public List<ProtoFIeld> fieldList = new List<ProtoFIeld>();
    }

    public class ProtoEnum
    {
        public string enumName = "";
        public Dictionary<string, int> valDict = new Dictionary<string, int>();
    }

}