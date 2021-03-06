﻿using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using Innovator.Client;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace InnovatorAdmin.Editor
{
  public class AmlSimpleEditorHelper : XmlEditorHelper
  {
    protected IAsyncConnection _conn;
    protected InnovatorAdmin.Connections.ConnectionData _connData;

    public AmlSimpleEditorHelper() : base()
    {
      _foldingStrategy = new AmlFoldingStrategy() { ShowAttributesWhenFolded = true };
    }

    internal static IHighlightingDefinition _highlighter;

    static AmlSimpleEditorHelper()
    {
      using (var stream = System.Reflection.Assembly.GetExecutingAssembly()
        .GetManifestResourceStream("InnovatorAdmin.resources.Aml.xshd"))
      {
        using (var reader = new System.Xml.XmlTextReader(stream))
        {
          _highlighter =
              ICSharpCode.AvalonEdit.Highlighting.Xshd.HighlightingLoader.Load(reader,
              ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance);
        }
      }
    }

    public override IHighlightingDefinition GetHighlighting()
    {
      return _highlighter;
    }

    public override IEnumerable<IEditorScript> GetScripts(ICSharpCode.AvalonEdit.Document.ITextSource text, int offset)
    {
      var item = GetCurrentItem(text, offset);
      if (item != null)
      {
        var generator = new ScriptMenuGenerator()
        {
          Conn = _conn,
          ConnData = _connData,
          Items = new[] { item }
        };
        return generator.GetScripts().Concat(Enumerable.Repeat(new EditorScriptExecute() {
          Name = "Transform: Criteria to Where Clause",
          Execute = () =>
          {
            var doc = text as IDocument;
            if (doc != null)
            {
              var segment = GetCurrentQuerySegment(text, offset);
              var elem = XElement.Load(text.CreateReader(segment.Offset, segment.Length));
              AmlTransforms.CriteriaToWhereClause(elem);
              doc.Replace(segment.Offset, segment.Length, elem.ToString());
            }
          }
        }, 1));
      }
      return Enumerable.Empty<IEditorScript>();
    }


    public override IEnumerable<IEditorScript> GetScripts(IEnumerable<DataRow> rows, string column)
    {
      var generator = new ScriptMenuGenerator()
      {
        Column = column,
        Conn = _conn,
        ConnData = _connData,
        Items = rows.Select(r => new DataRowItemData(r))
      };
      return generator.GetScripts();
    }

    private EditorItemData GetCurrentItem(ITextSource text, int offset)
    {
      var result = new Stack<EditorItemData>();

      XmlUtils.ProcessFragment(text, (r, o, st) =>
      {
        if (o > offset)
          return false;

        switch (r.NodeType)
        {
          case XmlNodeType.Element:

            if (r.LocalName == "Item")
            {
              result.Push(new EditorItemData()
              {
                Action = r.GetAttribute("action"),
                Type = r.GetAttribute("type"),
                Id = r.GetAttribute("id")
              });

              if (r.IsEmptyElement)
              {
                var end = text.IndexOf("/>", o, text.TextLength - o, StringComparison.Ordinal) + 2;
                if (offset >= o && offset < end)
                {
                  return false;
                }
                else
                {
                  result.Pop();
                }
              }
            }
            break;
          case XmlNodeType.EndElement:
            if (r.LocalName == "Item")
            {
              if (offset < (o + 6))
                return false;
              result.Pop();
            }
            break;
        }
        return true;
      });

      if (result.Any())
        return result.Pop();
      return null;
    }


    private interface IItemData
    {
      string Type { get; }
      string Id { get; }
      object Property(string name);
    }

    private class EditorItemData : IItemData
    {
      public string Type { get; set; }
      public string Id { get; set; }
      public string Action { get; set; }

      public object Property(string name)
      {
        return null;
      }
    }
    private class DataRowItemData : IItemData
    {
      private DataRow _row;

      public DataRowItemData(DataRow row)
      {
        _row = row;
      }

      public string Id { get { return (string)_row["id"]; } }
      public string Type { get { return (string)_row[Extensions.AmlTable_TypeName]; } }

      public object Property(string name)
      {

        if (_row.Table.Columns.Contains(name) && !_row.IsNull(name))
          return _row[name];
        return null;
      }
      public void SetProperty(string name, object value)
      {
        _row[name] = value;
        if (_row.RowState == DataRowState.Detached)
          _row.Table.Rows.Add(_row);
      }

      public void Delete()
      {
        if (_row.RowState != DataRowState.Detached)
          _row.Delete();
      }
    }

    private class ScriptMenuGenerator
    {
      public IAsyncConnection Conn { get; set; }
      public Connections.ConnectionData ConnData { get; set; }
      public string Column { get; set; }
      public IEnumerable<IItemData> Items { get; set; }

      public IEnumerable<IEditorScript> GetScripts()
      {
        var items = (Items ?? Enumerable.Empty<IItemData>())
          .Where(i => !string.IsNullOrEmpty(i.Id) && !string.IsNullOrEmpty(i.Type))
          .ToArray();
        if (!items.Any())
          yield break;

        if (items.Skip(1).Any()) // There is more than one
        {
          if (items.OfType<DataRowItemData>().Any())
          {
            yield return new EditorScriptExecute()
            {
              Name = "Delete",
              Execute = () =>
              {
                foreach (var row in items.OfType<DataRowItemData>())
                {
                  row.Delete();
                }
              }
            };
          }
          else
          {
            var builder = new StringBuilder("<AML>");
            foreach (var item in items)
            {
              builder.AppendLine().AppendFormat("  <Item type='{0}' id='{1}' action='delete'></Item>", item.Type, item.Id);
            }
            builder.AppendLine().Append("</AML>");
            yield return new EditorScript()
            {
              Name = "Delete",
              Action = "ApplyAML",
              Script = builder.ToString()
            };
          }
          yield return new EditorScript()
          {
            Name = "------"
          };
          yield return new EditorScriptExecute()
          {
            Name = "Export",
            Execute = () =>
            {
              var refs = items.Select(i => new ItemReference(i.Type, i.Id));
              StartExport(refs);
            }
          };
        }
        else
        {
          var item = items.Single();
          var rowItem = item as DataRowItemData;

          ArasMetadataProvider metadata = null;
          ItemType itemType = null;
          if (Conn != null)
          {
            metadata = ArasMetadataProvider.Cached(Conn);
            if (!metadata.ItemTypeByName(item.Type, out itemType))
              metadata = null;
          }

          if (metadata != null)
          {
            yield return new EditorScript()
            {
              Name = "View \"" + (itemType.Label ?? itemType.Name) + "\"",
              Action = "ApplyItem",
              Script = string.Format("<Item type='{0}' id='{1}' action='get' levels='1'></Item>", item.Type, item.Id),
              AutoRun = true,
              PreferredOutput = OutputType.Table
            };
            if (item.Property("related_id") != null && itemType.Related != null)
            {
              yield return new EditorScript()
              {
                Name = "View \"" + (itemType.Related.Label ?? itemType.Related.Name) + "\"",
                Action = "ApplyItem",
                Script = string.Format("<Item type='{0}' id='{1}' action='get' levels='1'></Item>", itemType.Related.Name, item.Property("related_id")),
                AutoRun = true,
                PreferredOutput = OutputType.Table
              };
            }
          }
          yield return new EditorScript()
          {
            Name = "------"
          };
          if (Conn != null)
          {
            yield return ArasEditorProxy.ItemTypeAddScript(Conn, itemType);
          }
          if (rowItem == null)
          {
            yield return new EditorScript()
            {
              Name = "Edit",
              Action = "ApplyItem",
              Script = string.Format("<Item type='{0}' id='{1}' action='edit'></Item>", item.Type, item.Id)
            };
            yield return new EditorScript()
            {
              Name = "Delete",
              Action = "ApplyItem",
              Script = string.Format("<Item type='{0}' id='{1}' action='delete'></Item>", item.Type, item.Id)
            };
          }
          else
          {
            if (!string.IsNullOrEmpty(Column))
            {
              var prop = metadata.GetProperty(itemType, Column.Split('/')[0]).Wait();
              switch (prop.Type)
              {
                case PropertyType.item:
                  // TODO: Handle getting items via the EditorWindow
                  //yield return new EditorScriptExecute()
                  //{
                  //  Name = "Edit Value",
                  //  Execute = () =>
                  //  {
                  //    var query = string.Format("<Item type='{0}' action='get'><keyed_name condition='like'>**</keyed_name></Item>", prop.Restrictions.First());
                  //    var values = EditorWindow.GetItems(Conn, query, query.Length - 21);
                  //    var results = values.Where(i => prop.Restrictions.Contains(i.Type)).ToArray();
                  //    if (results.Length == 1)
                  //    {
                  //      rowItem.SetProperty(prop.Name, results[0].Unique);
                  //      rowItem.SetProperty(prop.Name + "/keyed_name", results[0].KeyedName);
                  //      rowItem.SetProperty(prop.Name + "/type", results[0].Type);
                  //    }
                  //  }
                  //};
                  break;
              }
            }
            yield return new EditorScriptExecute()
            {
              Name = "Delete",
              Execute = () => rowItem.Delete()
            };
          }
          yield return new EditorScript()
          {
            Name = "------"
          };
          yield return new EditorScriptExecute()
          {
            Name = "Export",
            Execute = () =>
            {
              var refs = new[] { new ItemReference(item.Type, item.Id) };
              StartExport(refs);
            }
          };
          if (metadata != null)
          {
            var actions = new EditorScript()
            {
              Name = "Actions"
            };

            var serverActions = metadata.ServerItemActions(item.Type)
              .OrderBy(l => l.Label ?? l.Value, StringComparer.CurrentCultureIgnoreCase)
              .ToArray();
            foreach (var action in serverActions)
            {
              actions.Add(new EditorScript()
              {
                Name = (action.Label ?? action.Value),
                Action = "ApplyItem",
                Script = string.Format("<Item type='{0}' id='{1}' action='{2}'></Item>", item.Type, item.Id, action.Value),
                AutoRun = true
              });
            }

            if (serverActions.Any())
              yield return actions;

            var reports = new EditorScript()
            {
              Name = "Reports"
            };

            var serverReports = metadata.ServerReports(item.Type)
              .OrderBy(l => l.Label ?? l.Value, StringComparer.CurrentCultureIgnoreCase)
              .ToArray();
            foreach (var report in serverReports)
            {
              reports.Add(new EditorScript()
              {
                Name = (report.Label ?? report.Value),
                Action = "ApplyItem",
                Script = @"<Item type='Method' action='Run Report'>
  <report_name>" + report.Value + @"</report_name>
  <AML>
    <Item type='" + itemType.Name + "' typeId='" + itemType.Id + "' id='" + item.Id + @"' />
  </AML>
</Item>",
                AutoRun = true
              });
            }

            if (serverReports.Any())
              yield return reports;
          }
          yield return new EditorScriptExecute()
          {
            Name = "Copy ID",
            Execute = () =>
            {
              System.Windows.Clipboard.SetText(item.Id);
            }
          };
        }
      }

      private void StartExport(IEnumerable<ItemReference> selectedRefs)
      {
        if (Conn == null)
          return;

        //TODO: Support Exports
        //var main = new Main();
        //var wizard = (IWizard)main;
        //wizard.ConnectionInfo = new[] { ConnData };
        //wizard.Connection = Conn;

        //var prog = new InnovatorAdmin.Controls.ProgressStep<ExportProcessor>(wizard.ExportProcessor);
        //prog.MethodInvoke = e =>
        //{
        //  wizard.InstallScript = new InstallScript();
        //  wizard.InstallScript.ExportUri = new Uri(wizard.ConnectionInfo.First().Url);
        //  wizard.InstallScript.ExportDb = wizard.ConnectionInfo.First().Database;
        //  wizard.InstallScript.Lines = Enumerable.Empty<InstallItem>();
        //  e.Export(wizard.InstallScript, selectedRefs, true);
        //};
        //prog.GoNextAction = () => wizard.GoToStep(new Controls.ExportResolve());
        //main.Show();
        //wizard.GoToStep(prog);
      }
    }
  }
}
