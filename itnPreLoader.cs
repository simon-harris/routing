using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace ItnPreLoader
{
    class Program
    {
        static void Main(string[] args)
        {
            string sourceFolderPath = null;
            string destFolderPath = null;

            if (args.Count() == 0)
            {
                Logger("Missing source directory");
            }
            else
            {
                sourceFolderPath = args[0];
                destFolderPath = sourceFolderPath + "\\out";
            }

            Logger(string.Format("Removing grade separation"));
            Logger(string.Empty);
            Logger(string.Format("Output folder is {0}", destFolderPath));
            Logger(string.Empty);

            if (!Directory.Exists(sourceFolderPath))
            {
                Logger(string.Format("Source directory {0} does not exist. Stopping", sourceFolderPath));
            }

            if (Directory.Exists(destFolderPath))
            {
                Directory.Delete(destFolderPath, true);
            }

            Directory.CreateDirectory(destFolderPath);

            foreach (string gzFile in Directory.GetFiles(sourceFolderPath, "*.gz")) // e.g. 93701-SU0400-5c647.gz
            {
                DateTime start = DateTime.Now;

                string fileName = Path.GetFileNameWithoutExtension(gzFile);
                string gmlPreOutputFilePath = sourceFolderPath + "\\" + fileName + ".gml";
                string gmlOutputFilePath = destFolderPath + "\\" + fileName + ".gml";
                string gzOutputFilePath = destFolderPath + "\\" + fileName + ".gz";

                Logger(Path.GetFileName(gzFile));

                ItnGml itnGml = null;

                try
                {
                    using (var inStream = new GZipStream(File.OpenRead(gzFile), CompressionMode.Decompress))
                    {
                        itnGml = new ItnGml() { Gml = XElement.Load(inStream), Logger = Logger };

                        ////SaveXmlToFile(itnGml.Gml, gmlPreOutputFilePath, false); // for diagnostics

                        for (int level = 1; level <= 3; level++)
                        {
                            itnGml.RemoveGradeSeparationNodes(level);
                        }
                    }

                    ////SaveXmlToFile(itnGml.Gml, gmlOutputFilePath, false); // for diagnostics
                    SaveXmlToFile(itnGml.Gml, gzOutputFilePath, true);
                }
                catch (Exception ex)
                {
                    Logger(ex.ToString());
                }

                Logger(string.Format("{0}ms", (DateTime.Now - start).TotalMilliseconds));
                Logger(string.Empty);

            }

            Logger("Done. Press any key to continue.");
            Console.ReadKey();
        }

        public static void Logger(string message)
        {
            Console.WriteLine(message);
        }

        private static void SaveXmlToFile(XElement xml, string filePath, bool compress)
        {
            if (compress)
            {
                using (GZipStream gzipOutputStream = new GZipStream(new FileStream(filePath, FileMode.Create), CompressionMode.Compress))
                {
                    xml.Save(gzipOutputStream);
                }
            }
            else
            {
                using (var gmlOutputStream = new FileStream(filePath, FileMode.Create))
                {
                    xml.Save(gmlOutputStream);
                }
            }
        }
    }

    public class ItnGml
    {
        public XElement Gml { get; set; }

        public Action<string> Logger { private get; set; }

        // XML namespaces
        XNamespace osgb = @"http://www.ordnancesurvey.co.uk/xml/namespaces/osgb";
        XNamespace xlink = @"http://www.w3.org/1999/xlink";
        XNamespace gmlNs = @"http://www.opengis.net/gml";

        /// <summary>
        /// Remove nodes with the specified grade separation.        
        /// </summary>        
        public void RemoveGradeSeparationNodes(int gradeLevel)
        {
            if (gradeLevel == 0 || gradeLevel > 3)
            {
                throw new ArgumentOutOfRangeException("Level should be in the range 1 to 3");
            }

            while (true)
            {
                // In road links, find all directed nodes where there is a grade separation of the specified level.
                var raisedDirectedNodes = from el in Gml.Descendants(osgb + "directedNode")
                                          where el.Attribute("gradeSeparation") != null &&
                                                el.Attribute("gradeSeparation").Value == gradeLevel.ToString()
                                          select el;

                if (raisedDirectedNodes.Count() == 0)
                {
                    break;
                }

                // Each raised node is connected to two road links - find the pairs.            
                Dictionary<string, List<XElement>> roadLinkPairs = MatchRoadLinkPairs(raisedDirectedNodes);

                if (roadLinkPairs.Count() == 0)
                {
                    break;
                }

                // Join the two links together into link0 and delete link1
                var pair = roadLinkPairs.First();
                MergeRoadLinks(pair);

                RemoveLinkFromRoad(pair.Value[1].Attribute("fid").Value);

                // Delete the link1 element because link0 and link1 are now merged               
                pair.Value[1].Remove();               
            }
        }

        /// <summary>
        /// Build a collection of node id vs. attached road links.
        /// </summary>        
        private Dictionary<string, List<XElement>> MatchRoadLinkPairs(IEnumerable<XElement> raisedDirectedNodes)
        {
            var roadLinkPairs = new Dictionary<string, List<XElement>>();
            var retPairs = new Dictionary<string, List<XElement>>();

            foreach (var node in raisedDirectedNodes)
            {
                string nodeFid = node.Attribute(xlink + "href").Value;
                XElement roadLink = node.Parent;                

                if (roadLinkPairs.Keys.Contains(nodeFid))
                {
                    // The node exists, add this roadLink if it's not there already.
                    if (!roadLinkPairs[nodeFid].Contains(roadLink))
                    {
                        roadLinkPairs[nodeFid].Add(roadLink);
                    }
                }
                else
                {
                    // create a new pair and add the first roadLink
                    roadLinkPairs.Add(nodeFid, new List<XElement>() { roadLink });
                }
            }

            foreach (var pair in roadLinkPairs)
            {
                switch (pair.Value.Count())
                {
                    case 0: // No links.  This should not be possible.
                        Logger("No pairs");
                        break;

                    case 1: // Orphaned link
                        // This happens at the edge of a tile when a link from the neighbouring tile has been included to maintain referential integrity of this file's data.
                        // The raised link will be processed as part of its own tile so it can be removed from this one.
                        // The downside is that the link will be missing if it lies at the edge of the overall map coverage.
                        Logger(string.Format("Removing orphaned link {0}", pair.Value[0].Attribute("fid").Value));
                        pair.Value[0].Remove();
                        break;

                    case 2: // Paired node.  This is the normal situation.
                        retPairs.Add(pair.Key, pair.Value);                        
                        break;

                    default: // This is abnormal.  Something has gone wrong.
                        Logger("Too many links");

                        foreach (var l in pair.Value)
                        {
                            Logger(string.Format("l0:{0}", l.Attribute("fid").Value));
                        }

                        break;
                }
            }

            return retPairs;
        }

        /// <summary>
        /// Merge link0 and link1 and return the result.
        /// </summary>        
        private void MergeRoadLinks(KeyValuePair<string, List<XElement>> pair)
        {
            Logger(string.Format("Merging {0} with {1}", pair.Value[0].Attribute("fid").Value, pair.Value[1].Attribute("fid").Value));

            // Set link0's length to the sum of the pair's lengths
            pair.Value[0].Element(osgb + "length").Value = (double.Parse(pair.Value[0].Element(osgb + "length").Value) +
                                                            double.Parse(pair.Value[1].Element(osgb + "length").Value)).ToString();

            var sharedNodeFromLink0 = (from x in pair.Value[0].Elements(osgb + "directedNode")
                                       where x.Attribute(xlink + "href").Value == pair.Key
                                       select x).First();

            var sharedNodeFromLink1 = (from x in pair.Value[1].Elements(osgb + "directedNode")
                                       where x.Attribute(xlink + "href").Value == pair.Key
                                       select x).First();

            var nonSharedNodeFromLink1 = (from x in pair.Value[1].Elements(osgb + "directedNode")
                                          where x.Attribute(xlink + "href").Value != pair.Key
                                          select x).First();

            // Replace Link0's shared node with link1's unshared node.            
            sharedNodeFromLink0.Attribute(xlink + "href").Value = nonSharedNodeFromLink1.Attribute(xlink + "href").Value;

            if (nonSharedNodeFromLink1.Attribute("gradeSeparation") == null)
            {
                sharedNodeFromLink0.Attribute("gradeSeparation").Remove();
            }
            else
            {
                sharedNodeFromLink0.Attribute("gradeSeparation").Value = nonSharedNodeFromLink1.Attribute("gradeSeparation").Value;
            }

            // Copy the coordinate string into a list of strings to make manipulation easier.
            List<string> coordinates0 = pair.Value[0].Descendants(gmlNs + "coordinates").First().Value.Split(' ').ToList();
            List<string> coordinates1 = pair.Value[1].Descendants(gmlNs + "coordinates").First().Value.Split(' ').ToList();

            // Preserve link direction when joining links
            //
            //        link0       |        link1        |   action
            // -------------------|---------------------|-----------------------
            //  '-' ========> '+' | '-' ========> '+'   | link0 + link1
            //  '-' ========> '+' | '+' <======== '-'   | link0 + reverse(link1)
            //  '+' <======== '-' | '-' ========> '+'   | reverse(link1) + link0            
            //  '+' <======== '-' | '+' <======== '-'   | link1 + link0
            // -------------------|---------------------|-----------------------
            if (sharedNodeFromLink0.Attribute("orientation").Value == "+")
            {
                // Remove duplicate coordinate at end of link0
                coordinates0.RemoveAt(coordinates0.Count - 1);

                if (sharedNodeFromLink1.Attribute("orientation").Value == "+")
                {
                    coordinates1.Reverse();
                }

                coordinates0.AddRange(coordinates1);
            }
            else
            {
                // Remove duplicate coordinate at start of link0
                coordinates0.RemoveAt(0);

                if (sharedNodeFromLink1.Attribute("orientation").Value == "-")
                {
                    coordinates1.Reverse();
                }

                coordinates1.AddRange(coordinates0);
                coordinates0 = coordinates1;
            }

            // Convert the list back to a coordinate string.
            pair.Value[0].Descendants(gmlNs + "coordinates").First().Value = string.Join(" ", coordinates0.ToArray<string>());
        }

        /// <summary>
        /// Remove the link from its parent road.
        /// </summary>
        private void RemoveLinkFromRoad(string roadLinkFid)
        {
            var roads = from el in Gml.Descendants(osgb + "road") select el;

            foreach (var road in roads)
            {
                XElement roadLinkToDelete = (from rl in road.Elements(osgb + "networkMember")
                                             where rl.Value == roadLinkFid
                                             select rl).First();

                roadLinkToDelete.Remove();
            }
        }
    }
}
