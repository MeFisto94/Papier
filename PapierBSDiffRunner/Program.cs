using System.Collections.Generic;
using System.IO;
using System.Linq;
using deltaq.BsDiff;

namespace PapierBSDiffRunner
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            /*args.AsParallel().ForAll(*/
            args.ToList().ForEach(dll =>
            {
                using (var inputStream = File.Open($"{dll}.dll", FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (var output = File.Open($"{dll}-patched.dll", FileMode.Create, FileAccess.Write,
                        FileShare.Read))
                    {
                        BsPatch.Apply(inputStream, (offset, length) =>
                            {
                                var stream = File.Open($"{dll}-bsdiff.dll", FileMode.Open, FileAccess.Read,
                                    FileShare.Read);
                                stream.Position = offset;
                                // TODO: Is the length relevant or only related to opening/allocating a buffer?
                                return stream;
                            }, output);
                    }
                }
            });
        }
    }
}