using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PioneerFramework.Megastructures
{
    public static class MegastructureCodec
    {
        private static readonly JsonSerializerOptions Options=CreateOptions();
        private static JsonSerializerOptions CreateOptions()
        {
            var options=new JsonSerializerOptions(); options.Converters.Add(new VectorConverter()); return options;
        }
        public static string Encode<T>(T value)=>JsonSerializer.Serialize(value,Options);
        public static FrameworkDocument Decode(string text)=>JsonSerializer.Deserialize<FrameworkDocument>(text,Options)??throw new FormatException("Missing framework document.");
        public static T Clone<T>(T value)=>JsonSerializer.Deserialize<T>(Encode(value),Options)!;
        public static T Read<T>(string text)=>JsonSerializer.Deserialize<T>(text,Options)??throw new FormatException("Missing payload.");
        private sealed class VectorConverter:JsonConverter<DVector3>
        {
            public override DVector3 Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)
            {
                if(reader.TokenType!=JsonTokenType.StartArray) throw new JsonException();
                reader.Read(); double x=reader.GetDouble(); reader.Read();double y=reader.GetDouble();reader.Read();double z=reader.GetDouble();reader.Read();
                if(reader.TokenType!=JsonTokenType.EndArray) throw new JsonException();
                return new DVector3(x,y,z);
            }
            public override void Write(Utf8JsonWriter writer,DVector3 value,JsonSerializerOptions options)
            {writer.WriteStartArray();writer.WriteNumberValue(value.x);writer.WriteNumberValue(value.y);writer.WriteNumberValue(value.z);writer.WriteEndArray();}
        }
    }
    public readonly record struct RotationData(double X,double Y,double Z,double W)
    {
        public static RotationData Identity=>new(0,0,0,1);
        public static RotationData AxisAngle(DVector3 axis,double radians)
        {double sine=Math.Sin(radians/2);var n=axis.normalized;return new(n.x*sine,n.y*sine,n.z*sine,Math.Cos(radians/2));}
        public DVector3 Rotate(DVector3 vector)
        {var q=new DVector3(X,Y,Z);var t=2*DVector3.Cross(q,vector);return vector+W*t+DVector3.Cross(q,t);}
        public static RotationData operator*(RotationData a,RotationData b)=>new(a.W*b.X+a.X*b.W+a.Y*b.Z-a.Z*b.Y,a.W*b.Y-a.X*b.Z+a.Y*b.W+a.Z*b.X,a.W*b.Z+a.X*b.Y-a.Y*b.X+a.Z*b.W,a.W*b.W-a.X*b.X-a.Y*b.Y-a.Z*b.Z);
    }
}
