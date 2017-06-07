Imports System
Imports System.Collections.Generic
Imports System.Configuration
Imports System.Data.OleDb
Imports System.IO
Imports System.Linq
Imports System.Text.RegularExpressions
Imports System.Threading.Tasks
Imports ITHit.WebDAV.Server
Imports ITHit.WebDAV.Server.Acl
Imports ITHit.WebDAV.Server.Class1
Imports WebDAVServer.FileSystemStorage.HttpListener.ExtendedAttributes
Imports ITHit.WebDAV.Server.Search

''' <summary>
''' Folder in WebDAV repository.
''' </summary>
Public Class DavFolder
    Inherits DavHierarchyItem
    Implements IFolderAsync, ISearchAsync

    ''' <summary>
    ''' Windows Search Provider string.
    ''' </summary>
    Private Shared ReadOnly windowsSearchProvider As String = ConfigurationManager.AppSettings("WindowsSearchProvider")

    ''' <summary>
    ''' Corresponding instance of <see cref="DirectoryInfo"/> .
    ''' </summary>
    Private ReadOnly dirInfo As DirectoryInfo

    Public Shared Async Function GetFolderAsync(context As DavContext, path As String) As Task(Of DavFolder)
        Dim folderPath As String = context.MapPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar)
        Dim folder As DirectoryInfo = New DirectoryInfo(folderPath)
        If Not folder.Exists OrElse String.Compare(folder.FullName.TrimEnd(System.IO.Path.DirectorySeparatorChar), folderPath, StringComparison.OrdinalIgnoreCase) <> 0 Then
            Return Nothing
        End If

        Return New DavFolder(folder, context, path)
    End Function

    ''' <summary>
    ''' Initializes a new instance of this class.
    ''' </summary>
    ''' <param name="directory">Corresponding folder in the file system.</param>
    ''' <param name="context">WebDAV Context.</param>
    ''' <param name="path">Encoded path relative to WebDAV root folder.</param>
    Protected Sub New(directory As DirectoryInfo, context As DavContext, path As String)
        MyBase.New(directory, context, path.TrimEnd("/"c) & "/")
        dirInfo = directory
    End Sub

    Public Overridable Async Function GetChildrenAsync(propNames As IList(Of PropertyName)) As Task(Of IEnumerable(Of IHierarchyItemAsync)) Implements IItemCollectionAsync.GetChildrenAsync
        Dim children As IList(Of IHierarchyItemAsync) = New List(Of IHierarchyItemAsync)()
        Dim fileInfos As FileSystemInfo() = Nothing
        fileInfos = dirInfo.GetFileSystemInfos()
        For Each fileInfo As FileSystemInfo In fileInfos
            Dim childPath As String = Path & EncodeUtil.EncodeUrlPart(fileInfo.Name)
            Dim child As IHierarchyItemAsync = Await context.GetHierarchyItemAsync(childPath)
            If child IsNot Nothing Then
                children.Add(child)
            End If
        Next

        Return children
    End Function

    Public Async Function CreateFileAsync(name As String) As Task(Of IFileAsync) Implements IFolderAsync.CreateFileAsync
        Await RequireHasTokenAsync()
        Dim fileName As String = System.IO.Path.Combine(fileSystemInfo.FullName, name)
        Using stream As FileStream = New FileStream(fileName, FileMode.CreateNew)
        End Using

        Await context.socketService.NotifyRefreshAsync(Path)
        Return CType(Await context.GetHierarchyItemAsync(Path & EncodeUtil.EncodeUrlPart(name)), IFileAsync)
    End Function

    Overridable Public Async Function CreateFolderAsync(name As String) As Task Implements IFolderAsync.CreateFolderAsync
        Await RequireHasTokenAsync()
        dirInfo.CreateSubdirectory(name)
        Await context.socketService.NotifyRefreshAsync(Path)
    End Function

    Public Overrides Async Function CopyToAsync(destFolder As IItemCollectionAsync, destName As String, deep As Boolean, multistatus As MultistatusException) As Task Implements IHierarchyItemAsync.CopyToAsync
        Dim targetFolder As DavFolder = TryCast(destFolder, DavFolder)
        If targetFolder Is Nothing Then
            Throw New DavException("Target folder doesn't exist", DavStatus.CONFLICT)
        End If

        If IsRecursive(targetFolder) Then
            Throw New DavException("Cannot copy to subfolder", DavStatus.FORBIDDEN)
        End If

        Dim newDirLocalPath As String = System.IO.Path.Combine(targetFolder.FullPath, destName)
        Dim targetPath As String = targetFolder.Path & EncodeUtil.EncodeUrlPart(destName)
        Try
            If Not Directory.Exists(newDirLocalPath) Then
                Await targetFolder.CreateFolderAsync(destName)
            End If
        Catch ex As DavException
            ' Continue, but report error to client for the target item.
            multistatus.AddInnerException(targetPath, ex)
        End Try

        Dim createdFolder As IFolderAsync = CType(Await context.GetHierarchyItemAsync(targetPath), IFolderAsync)
        For Each item As DavHierarchyItem In Await GetChildrenAsync(New PropertyName(-1) {})
            If Not deep AndAlso TypeOf item Is DavFolder Then
                Continue For
            End If

            Try
                Await item.CopyToAsync(createdFolder, item.Name, deep, multistatus)
            Catch ex As DavException
                ' If a child item failed to copy we continue but report error to client.
                multistatus.AddInnerException(item.Path, ex)
            End Try
        Next

        Await context.socketService.NotifyRefreshAsync(targetFolder.Path)
    End Function

    Public Overrides Async Function MoveToAsync(destFolder As IItemCollectionAsync, destName As String, multistatus As MultistatusException) As Task Implements IHierarchyItemAsync.MoveToAsync
        Await RequireHasTokenAsync()
        Dim targetFolder As DavFolder = TryCast(destFolder, DavFolder)
        If targetFolder Is Nothing Then
            Throw New DavException("Target folder doesn't exist", DavStatus.CONFLICT)
        End If

        If IsRecursive(targetFolder) Then
            Throw New DavException("Cannot move folder to its subtree.", DavStatus.FORBIDDEN)
        End If

        Dim newDirPath As String = System.IO.Path.Combine(targetFolder.FullPath, destName)
        Dim targetPath As String = targetFolder.Path & EncodeUtil.EncodeUrlPart(destName)
        Try
            Dim item As IHierarchyItemAsync = Await context.GetHierarchyItemAsync(targetPath)
            If item IsNot Nothing Then Await item.DeleteAsync(multistatus)
            Await targetFolder.CreateFolderAsync(destName)
        Catch ex As DavException
            ' Continue the operation but report error with destination path to client.
            multistatus.AddInnerException(targetPath, ex)
            Return
        End Try

        Dim movedSuccessfully As Boolean = True
        Dim createdFolder As IFolderAsync = CType(Await context.GetHierarchyItemAsync(targetPath), IFolderAsync)
        For Each item As DavHierarchyItem In Await GetChildrenAsync(New PropertyName(-1) {})
            Try
                Await item.MoveToAsync(createdFolder, item.Name, multistatus)
            Catch ex As DavException
                ' Continue the operation but report error with child item to client.
                multistatus.AddInnerException(item.Path, ex)
                movedSuccessfully = False
            End Try
        Next

        If movedSuccessfully Then
            Await DeleteAsync(multistatus)
        End If

        Await context.socketService.NotifyDeleteAsync(Path)
        Await context.socketService.NotifyRefreshAsync(GetParentPath(targetPath))
    End Function

    Public Overrides Async Function DeleteAsync(multistatus As MultistatusException) As Task Implements IHierarchyItemAsync.DeleteAsync
        Await RequireHasTokenAsync()
        Dim allChildrenDeleted As Boolean = True
        For Each child As IHierarchyItemAsync In Await GetChildrenAsync(New PropertyName(-1) {})
            Try
                Await child.DeleteAsync(multistatus)
            Catch ex As DavException
                'continue the operation if a child failed to delete. Tell client about it by adding to multistatus.
                multistatus.AddInnerException(child.Path, ex)
                allChildrenDeleted = False
            End Try
        Next

        If allChildrenDeleted Then
            dirInfo.Delete()
            Await context.socketService.NotifyDeleteAsync(Path)
        End If
    End Function

    Public Async Function SearchAsync(searchString As String, options As SearchOptions, propNames As List(Of PropertyName)) As Task(Of IEnumerable(Of IHierarchyItemAsync)) Implements ISearchAsync.SearchAsync
        Dim includeSnippet As Boolean = propNames.Any(Function(s) s.Name = snippetProperty)
        Dim commandText As String = "SELECT System.ItemPathDisplay" & (If(includeSnippet, " ,System.Search.AutoSummary", String.Empty)) & " FROM SystemIndex " & "WHERE scope ='file:@Path' AND (System.ItemNameDisplay LIKE '@Name' OR FREETEXT('""@Content""')) " & "ORDER BY System.Search.Rank DESC"
        commandText = PrepareCommand(commandText,
                                    "@Path", Me.dirInfo.FullName,
                                    "@Name", searchString,
                                    "@Content", searchString)
        Dim foundItems As Dictionary(Of String, String) = New Dictionary(Of String, String)()
        Try
            Using connection As OleDbConnection = New OleDbConnection(windowsSearchProvider)
                Using command As OleDbCommand = New OleDbCommand(commandText, connection)
                    connection.Open()
                    Using reader As OleDbDataReader = command.ExecuteReader()
                        While Await reader.ReadAsync()
                            Dim snippet As String = String.Empty
                            If includeSnippet Then snippet = If(reader.GetValue(1) <> DBNull.Value, reader.GetString(1), Nothing)
                            foundItems.Add(reader.GetString(0), snippet)
                        End While
                    End Using
                End Using
            End Using
        Catch ex As OleDbException
            context.Logger.LogError(ex.Message, ex)
            Select Case ex.ErrorCode
                Case -2147217900
                    Throw New DavException("Illegal symbols in search phrase.", DavStatus.CONFLICT)
                Case Else
                    Throw New DavException("Unknown error.", DavStatus.INTERNAL_ERROR)
            End Select

        End Try

        Dim subtreeItems As IList(Of IHierarchyItemAsync) = New List(Of IHierarchyItemAsync)()
        For Each path As String In foundItems.Keys
            Dim item As IHierarchyItemAsync = Await context.GetHierarchyItemAsync(GetRelativePath(path))
            If includeSnippet AndAlso TypeOf item Is DavFile Then TryCast(item, DavFile).Snippet = HighlightKeywords(searchString.Trim("%"c), foundItems(path))
            subtreeItems.Add(item)
        Next

        Return subtreeItems
    End Function

    Private Function GetRelativePath(filePath As String) As String
        Dim itemPath As String = filePath.ToLower().Replace("\my documents\", "\documents\")
        Dim repoPath As String = Me.fileSystemInfo.FullName.ToLower().Replace("\my documents\", "\documents\")
        Dim relPathLength As Integer = itemPath.Substring(repoPath.Length).TrimStart("\"c).Length
        Dim relPath As String = filePath.Substring(filePath.Length - relPathLength)
        Dim encodedParts As IEnumerable(Of String) = relPath.Split("\"c).Select(AddressOf EncodeUtil.EncodeUrlPart)
        Return Me.Path & [String].Join("/", encodedParts.ToArray())
    End Function

    Private Shared Function HighlightKeywords(searchTerms As String, text As String) As String
        Dim exp As Regex = New Regex("\b(" & String.Join("|", searchTerms.Split(New Char() {","c, " "c}, StringSplitOptions.RemoveEmptyEntries)) & ")\b",
                                    RegexOptions.IgnoreCase Or RegexOptions.Multiline)
        Return exp.Replace(text, "<b>$0</b>")
    End Function

    Private Function PrepareCommand(commandText As String, ParamArray prms As Object()) As String
        If prms.Length Mod 2 <> 0 Then Throw New ArgumentException("Incorrect number of parameters")
        For i As Integer = 0 To prms.Length - 1 Step 2
            If Not(TypeOf prms(i) Is String) Then Throw New ArgumentException(prms(i) & "is invalid parameter name")
            Dim value As String = CStr(prms(i + 1))
            ' Search.CollatorDSO provider ignores ' and " chars, but we will remove them anyway
            value = value.Replace("""", [String].Empty)
            value = value.Replace("'", [String].Empty)
            commandText = commandText.Replace(CStr(prms(i)), value)
        Next

        Return commandText
    End Function

    Private Function IsRecursive(destFolder As DavFolder) As Boolean
        Return destFolder.Path.StartsWith(Path)
    End Function
End Class