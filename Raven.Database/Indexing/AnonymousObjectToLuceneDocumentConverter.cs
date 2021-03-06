//-----------------------------------------------------------------------
// <copyright file="AnonymousObjectToLuceneDocumentConverter.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Lucene.Net.Documents;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Raven.Abstractions;
using Raven.Abstractions.Data;
using Raven.Abstractions.Indexing;
using Raven.Abstractions.Linq;
using Raven.Database.Extensions;
using Raven.Database.Impl;
using Raven.Database.Linq;
using Raven.Json.Linq;
using DateTools = Lucene.Net.Documents.DateTools;

namespace Raven.Database.Indexing
{
	public static class AnonymousObjectToLuceneDocumentConverter
	{
		public static IEnumerable<AbstractField> Index(object val, PropertyDescriptorCollection properties, IndexDefinition indexDefinition, Field.Store defaultStorage)
		{
			return (from property in properties.Cast<PropertyDescriptor>()
			        let name = property.Name
					where name != Constants.DocumentIdFieldName
			        let value = property.GetValue(val)
			        from field in CreateFields(name, value, indexDefinition, defaultStorage)
			        select field);
		}

        public static IEnumerable<AbstractField> Index(RavenJObject document, IndexDefinition indexDefinition, Field.Store defaultStorage)
        {
        	return (from property in document
        	        let name = property.Key
					where name != Constants.DocumentIdFieldName
        	        let value = GetPropertyValue(property.Value)
        	        from field in CreateFields(name, value, indexDefinition, defaultStorage)
        	        select field);
        }

        private static object GetPropertyValue(RavenJToken property)
        {
            switch (property.Type)
            {
                case JTokenType.Array:
                case JTokenType.Object:
                    return property.ToString(Formatting.None);
                default:
                    return property.Value<object>();
            }
        }

		/// <summary>
		/// This method generate the fields for indexing documents in lucene from the values.
		/// Given a name and a value, it has the following behavior:
		/// * If the value is enumerable, index all the items in the enumerable under the same field name
		/// * If the value is null, create a single field with the supplied name with the unanalyzed value 'NULL_VALUE'
		/// * If the value is string or was set to not analyzed, create a single field with the supplied name
		/// * If the value is date, create a single field with millisecond precision with the supplied name
		/// * If the value is numeric (int, long, double, decimal, or float) will create two fields:
		///		1. with the supplied name, containing the numeric value as an unanalyzed string - useful for direct queries
		///		2. with the name: name +'_Range', containing the numeric value in a form that allows range queries
		/// </summary>
		private static IEnumerable<AbstractField> CreateFields(string name, object value, IndexDefinition indexDefinition, Field.Store defaultStorage)
		{
            if(string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Field must be not null, not empty and cannot contain whitespace", "name");

            if (char.IsLetter(name[0]) == false &&
                name[0] != '_')
            {
                name = "_" + name;
            }

			if (value == null)
			{
				yield return new Field(name, Constants.NullValue, indexDefinition.GetStorage(name, defaultStorage),
								 Field.Index.NOT_ANALYZED_NO_NORMS);
				yield break;
			}
			if (value is DynamicNullObject)
			{
				if(((DynamicNullObject)value ).IsExplicitNull)
				{
					yield return new Field(name, Constants.NullValue, indexDefinition.GetStorage(name, defaultStorage),
							 Field.Index.NOT_ANALYZED_NO_NORMS);
				}
				yield break;
			}

            if(value is AbstractField)
            {
                yield return (AbstractField)value;
                yield break;
            }


			var itemsToIndex = value as IEnumerable;
			if( itemsToIndex != null && ShouldTreatAsEnumerable(itemsToIndex))
			{
                yield return new Field(name + "_IsArray", "true", Field.Store.YES, Field.Index.NOT_ANALYZED_NO_NORMS);
                foreach (var itemToIndex in itemsToIndex)
				{
                    foreach (var field in CreateFields(name, itemToIndex, indexDefinition, defaultStorage))
                    {
                        yield return field;
                    }
				}
				yield break;
			}

			var fieldIndexingOptions = indexDefinition.GetIndex(name, null);
			if (fieldIndexingOptions == Field.Index.NOT_ANALYZED || fieldIndexingOptions == Field.Index.NOT_ANALYZED_NO_NORMS)// explicitly not analyzed
            {
				if (value is DateTime)
				{
				    var val = (DateTime) value;
					yield return new Field(name, val.ToString(Default.DateTimeFormatsToWrite), indexDefinition.GetStorage(name, defaultStorage),
									   indexDefinition.GetIndex(name, Field.Index.NOT_ANALYZED_NO_NORMS));
				}
				else if(value is DateTimeOffset)
				{
					var val = (DateTimeOffset)value;
					yield return new Field(name, val.ToString(Default.DateTimeFormatsToWrite), indexDefinition.GetStorage(name, defaultStorage),
									   indexDefinition.GetIndex(name, Field.Index.NOT_ANALYZED_NO_NORMS));
				}
				else
				{
					yield return new Field(name, value.ToString(), indexDefinition.GetStorage(name, defaultStorage),
										   indexDefinition.GetIndex(name, Field.Index.NOT_ANALYZED_NO_NORMS));
				}
            	yield break;
			    
            }
			if (value is string) 
			{
			    var index = indexDefinition.GetIndex(name, Field.Index.ANALYZED);
			    yield return new Field(name, value.ToString(), indexDefinition.GetStorage(name, defaultStorage),
                                 index); 
				yield break;
			}

			if (value is DateTime)
			{
				yield return new Field(name, DateTools.DateToString((DateTime)value, DateTools.Resolution.MILLISECOND),
					indexDefinition.GetStorage(name, defaultStorage),
					indexDefinition.GetIndex(name, Field.Index.NOT_ANALYZED_NO_NORMS));
			}
			else if (value is DateTimeOffset)
			{
				yield return new Field(name, DateTools.DateToString(((DateTimeOffset)value).DateTime, DateTools.Resolution.MILLISECOND),
					indexDefinition.GetStorage(name, defaultStorage),
					indexDefinition.GetIndex(name, Field.Index.NOT_ANALYZED_NO_NORMS));
			}
            else if(value is bool)
            {
                yield return new Field(name, ((bool) value) ? "true" : "false", indexDefinition.GetStorage(name, defaultStorage),
							  indexDefinition.GetIndex(name, Field.Index.NOT_ANALYZED_NO_NORMS));

            }
			else if(value is IConvertible) // we need this to store numbers in invariant format, so JSON could read them
			{
				var convert = ((IConvertible) value);
				yield return new Field(name, convert.ToString(CultureInfo.InvariantCulture), indexDefinition.GetStorage(name, defaultStorage),
									   indexDefinition.GetIndex(name, Field.Index.NOT_ANALYZED_NO_NORMS));
			}
			else if (value is DynamicJsonObject)
			{
				var inner = ((DynamicJsonObject)value).Inner;
				yield return new Field(name + "_ConvertToJson", "true", Field.Store.YES, Field.Index.NOT_ANALYZED_NO_NORMS);
				yield return new Field(name, inner.ToString(), indexDefinition.GetStorage(name, defaultStorage),
									   indexDefinition.GetIndex(name, Field.Index.NOT_ANALYZED_NO_NORMS));
			}
			else 
			{
				yield return new Field(name + "_ConvertToJson", "true", Field.Store.YES, Field.Index.NOT_ANALYZED_NO_NORMS);
				yield return new Field(name, RavenJToken.FromObject(value).ToString(), indexDefinition.GetStorage(name, defaultStorage),
									   indexDefinition.GetIndex(name, Field.Index.NOT_ANALYZED_NO_NORMS));
			}


			var numericField = new NumericField(name + "_Range", indexDefinition.GetStorage(name, defaultStorage), true);
			if (value is int)
			{
				if(indexDefinition.GetSortOption(name) == SortOptions.Long)
					yield return numericField.SetLongValue((int)value);
				else
					yield return numericField.SetIntValue((int)value);
			}
			if (value is long)
			{
				yield return numericField
					.SetLongValue((long) value);

			}
			if (value is decimal)
            {
				yield return numericField
					.SetDoubleValue((double)(decimal)value);
            }
			if (value is float)
            {
				if (indexDefinition.GetSortOption(name) == SortOptions.Double)
					yield return numericField.SetDoubleValue((float)value);
				else
            		yield return numericField.SetFloatValue((float) value);
            }
			if (value is double)
            {
				yield return numericField
					.SetDoubleValue((double)value);
            }
		}

	    private static bool ShouldTreatAsEnumerable(IEnumerable itemsToIndex)
	    {
            if (itemsToIndex == null)
                return false;

			if (itemsToIndex is DynamicJsonObject)
				return false;

            if (itemsToIndex is string)
                return false;

            if (itemsToIndex is RavenJObject)
                return false;

            if (itemsToIndex is IDictionary)
                return false;

	        return true;
	    }
	}
}
