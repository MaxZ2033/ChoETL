﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace ChoETL
{
    internal class ChoKVPRecordReader : ChoRecordReader
    {
        private IChoNotifyRecordRead _callbackRecord;
        private string[] _fieldNames = new string[] { };
        private bool _configCheckDone = false;
        internal ChoReader Reader = null;

        public ChoKVPRecordConfiguration Configuration
        {
            get;
            private set;
        }

        public ChoKVPRecordReader(Type recordType, ChoKVPRecordConfiguration configuration) : base(recordType)
        {
            ChoGuard.ArgumentNotNull(configuration, "Configuration");
            Configuration = configuration;
            _callbackRecord = ChoMetadataObjectCache.CreateMetadataObject<IChoNotifyRecordRead>(recordType);
            //Configuration.Validate();
        }

        public override void LoadSchema(object source)
        {
            var e = AsEnumerable(source, ChoETLFramework.TraceSwitchOff).GetEnumerator();
            e.MoveNext();
        }

        public override IEnumerable<object> AsEnumerable(object source, Func<object, bool?> filterFunc = null)
        {
            return AsEnumerable(source, TraceSwitch, filterFunc);
        }

        private IEnumerable<object> AsEnumerable(object source, TraceSwitch traceSwitch, Func<object, bool?> filterFunc = null)
        {
            TraceSwitch = traceSwitch;

            StreamReader sr = source as StreamReader;
            ChoGuard.ArgumentNotNull(sr, "StreamReader");

            sr.Seek(0, SeekOrigin.Begin);

            if (!RaiseBeginLoad(sr))
                yield break;

            string[] commentTokens = Configuration.Comments;
            bool? skip = false;
            bool isRecordStartFound = false;
            bool isRecordEndFound = false;
            long seekOriginPos = sr.BaseStream.Position;
            List<string> headers = new List<string>();
            Tuple<long, string> lastLine = null;
            List<Tuple<long, string>> recLines = new List<Tuple<long, string>>();
            long recNo = 0;
            int loopCount = Configuration.AutoDiscoverColumns && Configuration.KVPRecordFieldConfigurations.Count == 0 ? 2 : 1;
            bool isHeaderFound = loopCount == 1;
            bool IsHeaderLoaded = false;
            Tuple<long, string> pairIn;
            bool abortRequested = false;

            for (int i = 0; i < loopCount; i++)
            {
                if (i == 1)
                {
                    sr.Seek(seekOriginPos, SeekOrigin.Begin);
                    TraceSwitch = traceSwitch;
                }
                else
                    TraceSwitch = ChoETLFramework.TraceSwitchOff;
                lastLine = null;
                recLines.Clear();
                isRecordEndFound = false;
                isRecordStartFound = false;

                using (ChoPeekEnumerator<Tuple<long, string>> e = new ChoPeekEnumerator<Tuple<long, string>>(
                    new ChoIndexedEnumerator<string>(sr.ReadLines(Configuration.EOLDelimiter, Configuration.QuoteChar, Configuration.MayContainEOLInData)).ToEnumerable(),
                    (pair) =>
                    {
                    //bool isStateAvail = IsStateAvail();
                    skip = false;

                    //if (isStateAvail)
                    //{
                    //    if (!IsStateMatches(item))
                    //    {
                    //        skip = filterFunc != null ? filterFunc(item) : false;
                    //    }
                    //    else
                    //        skip = true;
                    //}
                    //else
                    //    skip = filterFunc != null ? filterFunc(item) : false;

                    if (skip == null)
                            return null;


                        if (TraceSwitch.TraceVerbose)
                        {
                            ChoETLFramework.WriteLog(TraceSwitch.TraceVerbose, Environment.NewLine);

                            if (!skip.Value)
                                ChoETLFramework.WriteLog(TraceSwitch.TraceVerbose, "Loading line [{0}]...".FormatString(pair.Item1));
                            else
                                ChoETLFramework.WriteLog(TraceSwitch.TraceVerbose, "Skipping line [{0}]...".FormatString(pair.Item1));
                        }

                        if (skip.Value)
                            return skip;

                    //if (!(sr.BaseStream is MemoryStream))
                    //    ChoETLFramework.WriteLog(TraceSwitch.TraceVerbose, ChoETLFramework.Switch.TraceVerbose, "Loading line [{0}]...".FormatString(item.Item1));

                    //if (Task != null)
                    //    return !IsStateNOTExistsOrNOTMatch(item);

                    if (pair.Item2.IsNullOrWhiteSpace())
                        {
                            if (!Configuration.IgnoreEmptyLine)
                                throw new ChoParserException("Empty line found at [{0}] location.".FormatString(pair.Item1));
                            else
                            {
                                if (TraceSwitch.TraceVerbose)
                                    ChoETLFramework.WriteLog(TraceSwitch.TraceVerbose, "Ignoring empty line found at [{0}].".FormatString(pair.Item1));
                                return true;
                            }
                        }

                        if (commentTokens != null && commentTokens.Length > 0)
                        {
                            foreach (string comment in commentTokens)
                            {
                                if (!pair.Item2.IsNull() && pair.Item2.StartsWith(comment, StringComparison.Ordinal)) //, true, Configuration.Culture))
                            {
                                    if (TraceSwitch.TraceVerbose)
                                        ChoETLFramework.WriteLog(TraceSwitch.TraceVerbose, "Comment line found at [{0}]...".FormatString(pair.Item1));
                                    return true;
                                }
                            }
                        }

                        if (!_configCheckDone)
                        {
                            Configuration.Validate(null);
                            _configCheckDone = true;
                        }

                        return false;
                    }))
                {
                    while (true)
                    {
                        pairIn = e.Peek;

                        if (!isRecordStartFound)
                        {
                            lastLine = null;
                            recLines.Clear();
                            isRecordEndFound = false;
                            isRecordStartFound = true;
                            if (!Configuration.RecordStart.IsNullOrWhiteSpace())
                            {
                                //Move to record start
                                while (!(Configuration.IsRecordStartMatch(e.Peek.Item2)))
                                {
                                    e.MoveNext();
                                    if (e.Peek == null)
                                        break;
                                }
                            }
                            if (e.Peek != null)
                                ChoETLFramework.WriteLog(TraceSwitch.TraceVerbose, "Record start found at [{0}] line...".FormatString(e.Peek.Item1));

                            e.MoveNext();
                            continue;
                        }
                        else
                        {
                            string recordEnd = !Configuration.RecordEnd.IsNullOrWhiteSpace() ? Configuration.RecordEnd : Configuration.RecordStart;
                            if (!recordEnd.IsNullOrWhiteSpace())
                            {
                                if (e.Peek == null)
                                {
                                    if (Configuration.RecordEnd.IsNullOrWhiteSpace())
                                        isRecordEndFound = true;
                                    else
                                        break;
                                }
                                else
                                {
                                    //Move to record start
                                    if (Configuration.IsRecordEndMatch(e.Peek.Item2))
                                    {
                                        isRecordEndFound = true;
                                        isRecordStartFound = false;
                                        if (e.Peek != null)
                                            ChoETLFramework.WriteLog(TraceSwitch.TraceVerbose, "Record end found at [{0}] line...".FormatString(e.Peek.Item1));
                                    }
                                }
                            }
                            else if (e.Peek == null)
                                isRecordEndFound = true;

                            if (!isHeaderFound)
                            {
                                //if (isRecordEndFound && headers.Count == 0)
                                //{
                                //    //throw new ChoParserException("Unexpected EOF found.");
                                //}
                                if (!isRecordEndFound)
                                {
                                    e.MoveNext();
                                    if (e.Peek != null)
                                    {
                                        //If line empty or line continuation, skip
                                        if (pairIn.Item2.IsNullOrWhiteSpace() || pairIn.Item2[0] == ' ' || pairIn.Item2[0] == '\t')
                                        {

                                        }
                                        else
                                        {
                                            string header = ToKVP(pairIn.Item1, pairIn.Item2).Key;
                                            if (!header.IsNullOrWhiteSpace())
                                                headers.Add(header);
                                        }
                                    }
                                }
                                else
                                {
                                    Configuration.Validate(headers.ToArray());
                                    isHeaderFound = true;
                                    isRecordEndFound = false;
                                    IsHeaderLoaded = true;
                                    break;
                                }
                            }
                            else
                            {
                                if (!IsHeaderLoaded)
                                {
                                    Configuration.Validate(new string[] { });
                                    IsHeaderLoaded = true;
                                }

                                if (isRecordEndFound && recLines.Count == 0)
                                {
                                    //throw new ChoParserException("Unexpected EOF found.");
                                }
                                else if (!isRecordEndFound)
                                {
                                    e.MoveNext();
                                    if (e.Peek != null)
                                    {
                                        //If line empty or line continuation, skip
                                        if (pairIn.Item2.IsNullOrWhiteSpace())
                                        {
                                            if (!Configuration.IgnoreEmptyLine)
                                                throw new ChoParserException("Empty line found at [{0}] location.".FormatString(pairIn.Item1));
                                            else
                                            {
                                                Tuple<long, string> t = new Tuple<long, string>(lastLine.Item1, lastLine.Item2 + Configuration.EOLDelimiter);
                                                recLines.RemoveAt(recLines.Count - 1);
                                                recLines.Add(t);
                                            }
                                        }
                                        else if (pairIn.Item2[0] == ' ' || pairIn.Item2[0] == '\t')
                                        {
                                            if (lastLine == null)
                                                throw new ChoParserException("Unexpected line continuation found at {0} location.".FormatString(pairIn.Item1));
                                            else
                                            {
                                                Tuple<long, string> t = new Tuple<long, string>(lastLine.Item1, lastLine.Item2 + Configuration.EOLDelimiter + pairIn.Item2);
                                                recLines.RemoveAt(recLines.Count - 1);
                                                recLines.Add(t);
                                            }
                                        }
                                        else
                                        {
                                            lastLine = pairIn;
                                            recLines.Add(pairIn);
                                        }
                                    }
                                }
                                else
                                {
                                    object rec = Activator.CreateInstance(RecordType);
                                    if (!LoadLines(new Tuple<long, List<Tuple<long, string>>>(++recNo, recLines), ref rec))
                                        yield break;

                                    //StoreState(e.Current, rec != null);

                                    if (!Configuration.RecordEnd.IsNullOrWhiteSpace())
                                        e.MoveNext();

                                    if (rec == null)
                                        continue;

                                    yield return rec;

                                    if (Configuration.NotifyAfter > 0 && recNo % Configuration.NotifyAfter == 0)
                                    {
                                        if (RaisedRowsLoaded(recNo))
                                        {
                                            ChoETLFramework.WriteLog(TraceSwitch.TraceVerbose, "Abort requested.");
                                            abortRequested = true;
                                            yield break;
                                        }
                                    }

                                    if (e.Peek == null)
                                        break;
                                }
                            }
                        }
                    }
                }
            }

            if (!abortRequested)
                RaisedRowsLoaded(recNo, true);
            RaiseEndLoad(sr);
        }

        private bool LoadLines(Tuple<long, List<Tuple<long, string>>> pairs, ref object rec)
        {
            ChoETLFramework.WriteLog(TraceSwitch.TraceVerbose, "Loading [{0}] record...".FormatString(pairs.Item1));

            Tuple<long, string> pair = null;
            long recNo = pairs.Item1;

            if (!Configuration.AutoDiscoveredColumns)
            {
                if (Configuration.ColumnCountStrict)
                {
                    if (pairs.Item2.Count != Configuration.KVPRecordFieldConfigurations.Count)
                        throw new ChoParserException("Incorrect number of field values found at record [{2}]. Expected [{0}] field values. Found [{1}] field values.".FormatString(Configuration.KVPRecordFieldConfigurations.Count, pairs.Item2.Count, recNo));
                    List<string> keys = (from p in pairs.Item2
                                         select ToKVP(p.Item1, p.Item2).Key).ToList();

                    if (!Enumerable.SequenceEqual(keys.OrderBy(t => t), Configuration.FCArray.Select(a => a.Value.FieldName).OrderBy(t => t), Configuration.FileHeaderConfiguration.StringComparer))
                    {
                        throw new ChoParserException("Column count mismatch detected at [{0}] record.".FormatString(recNo));
                    }
                }
                if (Configuration.ColumnOrderStrict)
                {
                    List<string> keys = (from p in pairs.Item2
                                         select ToKVP(p.Item1, p.Item2).Key).ToList();
                    int runnngIndex = -1;
                    foreach (var k in keys)
                    {
                        runnngIndex++;
                        if (runnngIndex < Configuration.FCArray.Length)
                        {
                            if (Configuration.FileHeaderConfiguration.IsEqual(Configuration.FCArray[runnngIndex].Value.FieldName, k))
                                continue;
                        }
                        throw new ChoParserException("Found incorrect order on '{1}' column at [{0}] record.".FormatString(recNo, k));
                    }
                }
            }

            object fieldValue = String.Empty;
            PropertyInfo pi = null;
            //Set default values
            foreach (KeyValuePair<string, ChoKVPRecordFieldConfiguration> kvp in Configuration.FCArray)
            {
                if (Configuration.PIDict != null)
                    Configuration.PIDict.TryGetValue(kvp.Key, out pi);
                try
                {
                    if (kvp.Value.IsDefaultValueSpecified)
                        fieldValue = kvp.Value.DefaultValue;

                    if (Configuration.IsDynamicObject)
                    {
                        var dict = rec as IDictionary<string, Object>;
                        dict.ConvertNSetMemberValue(kvp.Key, kvp.Value, ref fieldValue, Configuration.Culture);
                    }
                    else
                    {
                        if (pi != null)
                            rec.ConvertNSetMemberValue(kvp.Key, kvp.Value, ref fieldValue, Configuration.Culture);
                    }
                }
                catch
                {

                }
            }

            foreach (var pair1 in pairs.Item2)
            {
                pair = pair1;
                try
                {
                    if (!RaiseBeforeRecordLoad(rec, ref pair))
                    {
                        ChoETLFramework.WriteLog(TraceSwitch.TraceVerbose, "Skipping...");
                        rec = null;
                        return true;
                    }

                    if (pair.Item2 == null)
                    {
                        rec = null;
                        return true;
                    }
                    else if (pair.Item2 == String.Empty)
                        return true;

                    if (!pair.Item2.IsNullOrWhiteSpace())
                    {
                        if (!FillRecord(rec, pair))
                            return false;

                        if ((Configuration.ObjectValidationMode & ChoObjectValidationMode.ObjectLevel) == ChoObjectValidationMode.ObjectLevel)
                            rec.DoObjectLevelValidation(Configuration, Configuration.KVPRecordFieldConfigurations);
                    }

                    if (!RaiseAfterRecordLoad(rec, pair))
                        return false;
                }
                catch (ChoParserException)
                {
                    throw;
                }
                catch (ChoMissingRecordFieldException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ChoETLFramework.HandleException(ex);
                    if (Configuration.ErrorMode == ChoErrorMode.IgnoreAndContinue)
                    {
                        rec = null;
                    }
                    else if (Configuration.ErrorMode == ChoErrorMode.ReportAndContinue)
                    {
                        if (!RaiseRecordLoadError(rec, pair, ex))
                            throw;
                    }
                    else
                        throw;

                    return true;
                }
            }

            return true;
        }

        private KeyValuePair<string, string> ToKVP(long lineNo, string line)
        {
            if (Configuration.Seperator.Length == 1)
            {
                int pos = line.IndexOf(Configuration.Seperator[0]);
                if (pos <= 0)
                    throw new ChoMissingRecordFieldException("Missing key at '{0}' line.".FormatString(lineNo));
                return new KeyValuePair<string, string>(CleanKeyValue(line.Substring(0, pos)), line.Substring(pos + 1));
            }
            else
            {
                int pos = line.IndexOf(Configuration.Seperator);
                if (pos <= 0)
                    throw new ChoMissingRecordFieldException("Missing key at '{0}' line.".FormatString(lineNo));
                return new KeyValuePair<string, string>(CleanKeyValue(line.Substring(0, pos)), line.Substring(pos + Configuration.Seperator.Length));
            }
        }

        private string CleanFieldValue(ChoKVPRecordFieldConfiguration config, Type fieldType, string fieldValue)
        {
            if (fieldValue == null) return fieldValue;

            ChoFieldValueTrimOption fieldValueTrimOption = ChoFieldValueTrimOption.Trim;

            if (config.FieldValueTrimOption == null)
            {
                //if (fieldType == typeof(string))
                //    fieldValueTrimOption = ChoFieldValueTrimOption.None;
            }
            else
                fieldValueTrimOption = config.FieldValueTrimOption.Value;

            switch (fieldValueTrimOption)
            {
                case ChoFieldValueTrimOption.Trim:
                    fieldValue = fieldValue.Trim();
                    break;
                case ChoFieldValueTrimOption.TrimStart:
                    fieldValue = fieldValue.TrimStart();
                    break;
                case ChoFieldValueTrimOption.TrimEnd:
                    fieldValue = fieldValue.TrimEnd();
                    break;
            }

            if (config.Size != null)
            {
                if (fieldValue.Length > config.Size.Value)
                {
                    if (!config.Truncate)
                        throw new ChoParserException("Incorrect field value length found for '{0}' member [Expected: {1}, Actual: {2}].".FormatString(config.FieldName, config.Size.Value, fieldValue.Length));
                    else
                        fieldValue = fieldValue.Substring(0, config.Size.Value);
                }
            }

            char startChar;
            char endChar;
            char quoteChar = Configuration.QuoteChar == '\0' ? '"' : Configuration.QuoteChar;

            if (fieldValue.Length >= 2)
            {
                startChar = fieldValue[0];
                endChar = fieldValue[fieldValue.Length - 1];

                if (config.QuoteField != null && config.QuoteField.Value && startChar == quoteChar && endChar == quoteChar)
                    return fieldValue.Substring(1, fieldValue.Length - 2);
                else if (startChar == quoteChar && endChar == quoteChar &&
                    (fieldValue.Contains(Configuration.Seperator)
                    || fieldValue.Contains(Configuration.EOLDelimiter)))
                    return fieldValue.Substring(1, fieldValue.Length - 2);

            }
            return fieldValue;
        }

        private string CleanKeyValue(string key)
        {
            if (key.IsNull()) return key;

            ChoFileHeaderConfiguration config = Configuration.FileHeaderConfiguration;
            if (key != null)
            {
                switch (config.TrimOption)
                {
                    case ChoFieldValueTrimOption.Trim:
                        key = key.Trim();
                        break;
                    case ChoFieldValueTrimOption.TrimStart:
                        key = key.TrimStart();
                        break;
                    case ChoFieldValueTrimOption.TrimEnd:
                        key = key.TrimEnd();
                        break;
                }
            }

            if (Configuration.QuoteAllFields && key.StartsWith(@"""") && key.EndsWith(@""""))
                return key.Substring(1, key.Length - 2);
            else
                return key;
        }

        private bool FillRecord(object rec, Tuple<long, string> pair)
        {
            long lineNo;
            string line;

            lineNo = pair.Item1;
            line = pair.Item2;

            var tokens = ToKVP(pair.Item1, pair.Item2);

            //ValidateLine(pair.Item1, fieldValues);

            object fieldValue = tokens.Value;
            string key = tokens.Key;
            if (!Configuration.RecordFieldConfigurationsDict2.ContainsKey(key))
                return true;
            key = Configuration.RecordFieldConfigurationsDict2[key].Name;
            ChoKVPRecordFieldConfiguration fieldConfig = Configuration.RecordFieldConfigurationsDict[key];
            PropertyInfo pi = null;

            fieldValue = CleanFieldValue(fieldConfig, fieldConfig.FieldType, fieldValue as string);

            if (Configuration.IsDynamicObject)
            {
                if (fieldConfig.FieldType == null)
                    fieldConfig.FieldType = typeof(string);
            }
            else
            {
                if (pi != null)
                    fieldConfig.FieldType = pi.PropertyType;
                else
                    fieldConfig.FieldType = typeof(string);
            }

            fieldValue = CleanFieldValue(fieldConfig, fieldConfig.FieldType, fieldValue as string);

            if (!RaiseBeforeRecordFieldLoad(rec, pair.Item1, key, ref fieldValue))
                return true;

            try
            {
                bool ignoreFieldValue = fieldConfig.IgnoreFieldValue(fieldValue);
                if (ignoreFieldValue)
                    fieldValue = fieldConfig.IsDefaultValueSpecified ? fieldConfig.DefaultValue : null;

                if (Configuration.IsDynamicObject)
                {
                    var dict = rec as IDictionary<string, Object>;

                    dict.ConvertNSetMemberValue(key, fieldConfig, ref fieldValue, Configuration.Culture);

                    if ((Configuration.ObjectValidationMode & ChoObjectValidationMode.MemberLevel) == ChoObjectValidationMode.MemberLevel)
                        dict.DoMemberLevelValidation(key, fieldConfig, Configuration.ObjectValidationMode);
                }
                else
                {
                    if (pi != null)
                        rec.ConvertNSetMemberValue(key, fieldConfig, ref fieldValue, Configuration.Culture);
                    else
                        throw new ChoMissingRecordFieldException("Missing '{0}' property in {1} type.".FormatString(key, ChoType.GetTypeName(rec)));

                    if ((Configuration.ObjectValidationMode & ChoObjectValidationMode.MemberLevel) == ChoObjectValidationMode.MemberLevel)
                        rec.DoMemberLevelValidation(key, fieldConfig, Configuration.ObjectValidationMode);
                }

                if (!RaiseAfterRecordFieldLoad(rec, pair.Item1, key, fieldValue))
                    return false;
            }
            catch (ChoParserException)
            {
                throw;
            }
            catch (ChoMissingRecordFieldException)
            {
                if (Configuration.ThrowAndStopOnMissingField)
                    throw;
            }
            catch (Exception ex)
            {
                ChoETLFramework.HandleException(ex);

                if (fieldConfig.ErrorMode == ChoErrorMode.ThrowAndStop)
                    throw;

                try
                {
                    if (Configuration.IsDynamicObject)
                    {
                        var dict = rec as IDictionary<string, Object>;

                        if (dict.SetFallbackValue(key, fieldConfig, Configuration.Culture, ref fieldValue))
                            dict.DoMemberLevelValidation(key, fieldConfig, Configuration.ObjectValidationMode);
                        else if (dict.SetDefaultValue(key, fieldConfig, Configuration.Culture))
                            dict.DoMemberLevelValidation(key, fieldConfig, Configuration.ObjectValidationMode);
                        else
                            throw new ChoReaderException($"Failed to parse '{fieldValue}' value for '{fieldConfig.FieldName}' field.", ex);
                    }
                    else if (pi != null)
                    {
                        if (rec.SetFallbackValue(key, fieldConfig, Configuration.Culture))
                            rec.DoMemberLevelValidation(key, fieldConfig, Configuration.ObjectValidationMode);
                        else if (rec.SetDefaultValue(key, fieldConfig, Configuration.Culture))
                            rec.DoMemberLevelValidation(key, fieldConfig, Configuration.ObjectValidationMode);
                        else
                            throw new ChoReaderException($"Failed to parse '{fieldValue}' value for '{fieldConfig.FieldName}' field.", ex);
                    }
                    else
                        throw new ChoReaderException($"Failed to parse '{fieldValue}' value for '{fieldConfig.FieldName}' field.", ex);
                }
                catch (Exception innerEx)
                {
                    if (ex == innerEx.InnerException)
                    {
                        if (fieldConfig.ErrorMode == ChoErrorMode.IgnoreAndContinue)
                        {
                        }
                        else
                        {
                            if (!RaiseRecordFieldLoadError(rec, pair.Item1, key, fieldValue, ex))
                                throw new ChoReaderException($"Failed to parse '{fieldValue}' value for '{fieldConfig.FieldName}' field.", ex);
                        }
                    }
                    else
                    {
                        throw new ChoReaderException("Failed to assign '{0}' fallback value to '{1}' field.".FormatString(fieldValue, fieldConfig.FieldName), innerEx);
                    }
                }
            }

            return true;
        }

        #region Event Raisers

        private bool RaiseBeginLoad(object state)
        {
            if (_callbackRecord != null)
            {
                return ChoFuncEx.RunWithIgnoreError(() => _callbackRecord.BeginLoad(state), true);
            }
            else if (Reader != null)
            {
                return ChoFuncEx.RunWithIgnoreError(() => Reader.RaiseBeginLoad(state), true);
            }
            return true;
        }

        private void RaiseEndLoad(object state)
        {
            if (_callbackRecord != null)
            {
                ChoActionEx.RunWithIgnoreError(() => _callbackRecord.EndLoad(state));
            }
            else if (Reader != null)
            {
                ChoActionEx.RunWithIgnoreError(() => Reader.RaiseEndLoad(state));
            }
        }

        private bool RaiseBeforeRecordLoad(object target, ref Tuple<long, string> pair)
        {
            if (_callbackRecord != null)
            {
                long index = pair.Item1;
                object state = pair.Item2;
                bool retValue = ChoFuncEx.RunWithIgnoreError(() => _callbackRecord.BeforeRecordLoad(target, index, ref state), true);

                if (retValue)
                    pair = new Tuple<long, string>(index, state as string);

                return retValue;
            }
            else if (Reader != null)
            {
                long index = pair.Item1;
                object state = pair.Item2;
                bool retValue = ChoFuncEx.RunWithIgnoreError(() => Reader.RaiseBeforeRecordLoad(target, index, ref state), true);

                if (retValue)
                    pair = new Tuple<long, string>(index, state as string);

                return retValue;
            }
            return true;
        }

        private bool RaiseAfterRecordLoad(object target, Tuple<long, string> pair)
        {
            if (_callbackRecord != null)
            {
                return ChoFuncEx.RunWithIgnoreError(() => _callbackRecord.AfterRecordLoad(target, pair.Item1, pair.Item2), true);
            }
            else if (Reader != null)
            {
                return ChoFuncEx.RunWithIgnoreError(() => Reader.RaiseAfterRecordLoad(target, pair.Item1, pair.Item2), true);
            }
            return true;
        }

        private bool RaiseRecordLoadError(object target, Tuple<long, string> pair, Exception ex)
        {
            if (_callbackRecord != null)
            {
                return ChoFuncEx.RunWithIgnoreError(() => _callbackRecord.RecordLoadError(target, pair.Item1, pair.Item2, ex), false);
            }
            else if (Reader != null)
            {
                return ChoFuncEx.RunWithIgnoreError(() => Reader.RaiseRecordLoadError(target, pair.Item1, pair.Item2, ex), false);
            }
            return true;
        }

        private bool RaiseBeforeRecordFieldLoad(object target, long index, string propName, ref object value)
        {
            if (_callbackRecord != null)
            {
                object state = value;
                bool retValue = ChoFuncEx.RunWithIgnoreError(() => _callbackRecord.BeforeRecordFieldLoad(target, index, propName, ref state), true);

                if (retValue)
                    value = state;

                return retValue;
            }
            else if (Reader != null)
            {
                object state = value;
                bool retValue = ChoFuncEx.RunWithIgnoreError(() => Reader.RaiseBeforeRecordFieldLoad(target, index, propName, ref state), true);

                if (retValue)
                    value = state;

                return retValue;
            }
            return true;
        }

        private bool RaiseAfterRecordFieldLoad(object target, long index, string propName, object value)
        {
            if (_callbackRecord != null)
            {
                return ChoFuncEx.RunWithIgnoreError(() => _callbackRecord.AfterRecordFieldLoad(target, index, propName, value), true);
            }
            else if (Reader != null)
            {
                return ChoFuncEx.RunWithIgnoreError(() => Reader.RaiseAfterRecordFieldLoad(target, index, propName, value), true);
            }
            return true;
        }

        private bool RaiseRecordFieldLoadError(object target, long index, string propName, object value, Exception ex)
        {
            if (_callbackRecord != null)
            {
                return ChoFuncEx.RunWithIgnoreError(() => _callbackRecord.RecordFieldLoadError(target, index, propName, value, ex), true);
            }
            else if (Reader != null)
            {
                return ChoFuncEx.RunWithIgnoreError(() => Reader.RaiseRecordFieldLoadError(target, index, propName, value, ex), true);
            }
            return true;
        }

        #endregion Event Raisers
    }
}
