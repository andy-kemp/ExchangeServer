###################################################
# Exchange Certificate Request Generator          #
###################################################

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.Text = "Exchange Certificate Request Generator"
$form.Size = New-Object System.Drawing.Size(760, 850)
$form.StartPosition = "CenterScreen"
$form.MaximizeBox = $false

###################################################
# 1. Connect to Exchange Section                  #
###################################################

$lblConnectHeader = New-Object System.Windows.Forms.Label
$lblConnectHeader.Text = "Connect to Exchange"
$lblConnectHeader.Font = New-Object System.Drawing.Font("Segoe UI", 12, [System.Drawing.FontStyle]::Bold)
$lblConnectHeader.Location = New-Object System.Drawing.Point(20, 10)
$lblConnectHeader.AutoSize = $true
$form.Controls.Add($lblConnectHeader)

$lblUsername = New-Object System.Windows.Forms.Label
$lblUsername.Text = "Username:"
$lblUsername.Location = New-Object System.Drawing.Point(40, 45)
$lblUsername.AutoSize = $true
$form.Controls.Add($lblUsername)

$txtUsername = New-Object System.Windows.Forms.TextBox
$txtUsername.Location = New-Object System.Drawing.Point(130, 43)
$txtUsername.Width = 150
$form.Controls.Add($txtUsername)

$lblPassword = New-Object System.Windows.Forms.Label
$lblPassword.Text = "Password:"
$lblPassword.Location = New-Object System.Drawing.Point(300, 45)
$lblPassword.AutoSize = $true
$form.Controls.Add($lblPassword)

$txtPassword = New-Object System.Windows.Forms.TextBox
$txtPassword.Location = New-Object System.Drawing.Point(390, 43)
$txtPassword.Width = 150
$txtPassword.UseSystemPasswordChar = $true
$form.Controls.Add($txtPassword)

$lblServer = New-Object System.Windows.Forms.Label
$lblServer.Text = "Exchange Server:"
$lblServer.Location = New-Object System.Drawing.Point(40, 75)
$lblServer.AutoSize = $true
$form.Controls.Add($lblServer)

$txtServer = New-Object System.Windows.Forms.TextBox
$txtServer.Location = New-Object System.Drawing.Point(160, 73)
$txtServer.Width = 170
$form.Controls.Add($txtServer)

$lblServerExample = New-Object System.Windows.Forms.Label
$lblServerExample.Text = "(e.g., ex.ad.kemponline.co.uk)"
$lblServerExample.Font = New-Object System.Drawing.Font($lblServer.Font, [System.Drawing.FontStyle]::Italic)
$lblServerExample.Location = New-Object System.Drawing.Point(340, 75)
$lblServerExample.AutoSize = $true
$lblServerExample.ForeColor = [System.Drawing.Color]::Gray
$form.Controls.Add($lblServerExample)

$btnConnect = New-Object System.Windows.Forms.Button
$btnConnect.Text = "Connect"
$btnConnect.Location = New-Object System.Drawing.Point(590, 38)
$btnConnect.Size = New-Object System.Drawing.Size(120, 32)
$form.Controls.Add($btnConnect)

$btnDisconnect = New-Object System.Windows.Forms.Button
$btnDisconnect.Text = "Disconnect"
$btnDisconnect.Location = New-Object System.Drawing.Point(590, 73)
$btnDisconnect.Size = New-Object System.Drawing.Size(120, 32)
$btnDisconnect.Enabled = $false
$form.Controls.Add($btnDisconnect)

$lblConnectResult = New-Object System.Windows.Forms.Label
$lblConnectResult.Text = ""
$lblConnectResult.Location = New-Object System.Drawing.Point(40, 110)
$lblConnectResult.Width = 700
$lblConnectResult.ForeColor = [System.Drawing.Color]::Blue
$form.Controls.Add($lblConnectResult)

$global:Connected = $false
$global:Session = $null

function EnsureExchangeConnection {
    if ($global:Connected -and $global:Session) { return $true }
    $username = $txtUsername.Text
    $password = $txtPassword.Text
    $server = $txtServer.Text
    if (-not $username -or -not $password -or -not $server) {
        [System.Windows.Forms.MessageBox]::Show("Please complete all connection fields.", "Error", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
        return $false
    }
    try {
        $existingSessions = Get-PSSession | Where-Object { $_.ConfigurationName -eq "Microsoft.Exchange" }
        foreach ($sess in $existingSessions) {
            try { Remove-PSSession $sess -ErrorAction SilentlyContinue } catch {}
        }
    } catch {}
    $global:Session = $null
    $btnDisconnect.Enabled = $false
    try {
        $SecurePass = ConvertTo-SecureString $password -AsPlainText -Force
        $UserCredential = New-Object System.Management.Automation.PSCredential ($username, $SecurePass)
        $ExchangeServer = "http://$server/PowerShell/"
        $global:Session = New-PSSession -ConfigurationName Microsoft.Exchange -ConnectionUri $ExchangeServer -Authentication Kerberos -Credential $UserCredential -AllowRedirection
        Import-PSSession $global:Session -DisableNameChecking | Out-Null
        $lblConnectResult.ForeColor = [System.Drawing.Color]::Blue
        $lblConnectResult.Text = "Connected to Exchange Management Shell."
        $global:Connected = $true
        $btnDisconnect.Enabled = $true
        return $true
    } catch {
        $global:Connected = $false
        $lblConnectResult.ForeColor = [System.Drawing.Color]::Red
        $lblConnectResult.Text = "Connection failed: $($_.Exception.Message)"
        $btnDisconnect.Enabled = $false
        [System.Windows.Forms.MessageBox]::Show("Connection to Exchange failed: $($_.Exception.Message)", "Error", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
        return $false
    }
}

$btnConnect.Add_Click({
    if (EnsureExchangeConnection) { }
})

$btnDisconnect.Add_Click({
    if ($global:Session) {
        try {
            Remove-PSSession $global:Session
        } catch {}
        $global:Session = $null
    }
    try {
        $existingSessions = Get-PSSession | Where-Object { $_.ConfigurationName -eq "Microsoft.Exchange" }
        foreach ($sess in $existingSessions) {
            try { Remove-PSSession $sess -ErrorAction SilentlyContinue } catch {}
        }
    } catch {}
    $global:Connected = $false
    $lblConnectResult.ForeColor = [System.Drawing.Color]::DarkOrange
    $lblConnectResult.Text = "Disconnected from Exchange."
    $btnDisconnect.Enabled = $false
})

###################################################
# 2. Operation Mode                              #
###################################################

$lblOpMode = New-Object System.Windows.Forms.Label
$lblOpMode.Text = "Operation:"
$lblOpMode.Location = New-Object System.Drawing.Point(40, 140)
$lblOpMode.AutoSize = $true
$form.Controls.Add($lblOpMode)

$rbReq = New-Object System.Windows.Forms.RadioButton
$rbReq.Text = "New Certificate Request"
$rbReq.Location = New-Object System.Drawing.Point(150, 138)
$rbReq.Size = New-Object System.Drawing.Size(180, 24)
$rbReq.Checked = $true
$form.Controls.Add($rbReq)

$rbComplete = New-Object System.Windows.Forms.RadioButton
$rbComplete.Text = "Complete Certificate Request"
$rbComplete.Location = New-Object System.Drawing.Point(340, 138)
$rbComplete.Size = New-Object System.Drawing.Size(210, 24)
$form.Controls.Add($rbComplete)

function UpdateOpMode {
    if ($rbReq.Checked) {
        $grpReq.Enabled = $true
        $grpComplete.Enabled = $false
    } elseif ($rbComplete.Checked) {
        $grpReq.Enabled = $false
        $grpComplete.Enabled = $true
    }
}
$rbReq.Add_CheckedChanged({ UpdateOpMode })
$rbComplete.Add_CheckedChanged({ UpdateOpMode })

###################################################
# 3. Certificate Request Section                 #
###################################################

$grpReq = New-Object System.Windows.Forms.GroupBox
$grpReq.Text = "Generate New Certificate Request"
$grpReq.Location = New-Object System.Drawing.Point(20, 170)
$grpReq.Size = New-Object System.Drawing.Size(710, 200)
$form.Controls.Add($grpReq)

$lblCertType = New-Object System.Windows.Forms.Label
$lblCertType.Text = "Certificate Type:"
$lblCertType.Location = New-Object System.Drawing.Point(15, 28)
$lblCertType.AutoSize = $true
$lblCertType.Parent = $grpReq

$rbSAN = New-Object System.Windows.Forms.RadioButton
$rbSAN.Text = "SAN Certificate"
$rbSAN.Location = New-Object System.Drawing.Point(145, 26)
$rbSAN.Size = New-Object System.Drawing.Size(120, 22)
$rbSAN.Checked = $true
$rbSAN.Parent = $grpReq

$rbWildcard = New-Object System.Windows.Forms.RadioButton
$rbWildcard.Text = "Wildcard Certificate"
$rbWildcard.Location = New-Object System.Drawing.Point(275, 26)
$rbWildcard.Size = New-Object System.Drawing.Size(160, 22)
$rbWildcard.Parent = $grpReq

$lblFriendly = New-Object System.Windows.Forms.Label
$lblFriendly.Text = "Friendly Name:"
$lblFriendly.Location = New-Object System.Drawing.Point(15, 58)
$lblFriendly.AutoSize = $true
$lblFriendly.Parent = $grpReq

$txtFriendly = New-Object System.Windows.Forms.TextBox
$txtFriendly.Location = New-Object System.Drawing.Point(145, 56)
$txtFriendly.Width = 200
$txtFriendly.Parent = $grpReq

$lblCN = New-Object System.Windows.Forms.Label
$lblCN.Text = "Subject Name (e.g. CN=mail.domain.com):"
$lblCN.Location = New-Object System.Drawing.Point(15, 88)
$lblCN.AutoSize = $true
$lblCN.Parent = $grpReq

$txtCN = New-Object System.Windows.Forms.TextBox
$txtCN.Location = New-Object System.Drawing.Point(285, 86)
$txtCN.Width = 220
$txtCN.Parent = $grpReq

$lblSAN = New-Object System.Windows.Forms.Label
$lblSAN.Text = "Additional Domain Names (CSV):"
$lblSAN.Location = New-Object System.Drawing.Point(15, 118)
$lblSAN.AutoSize = $true
$lblSAN.Parent = $grpReq

$txtSAN = New-Object System.Windows.Forms.TextBox
$txtSAN.Location = New-Object System.Drawing.Point(225, 116)
$txtSAN.Width = 300
$txtSAN.Parent = $grpReq

$lblReqPath = New-Object System.Windows.Forms.Label
$lblReqPath.Text = "Request File (.req):"
$lblReqPath.Location = New-Object System.Drawing.Point(15, 148)
$lblReqPath.AutoSize = $true
$lblReqPath.Parent = $grpReq

$txtReqPath = New-Object System.Windows.Forms.TextBox
$txtReqPath.Location = New-Object System.Drawing.Point(145, 146)
$txtReqPath.Width = 300
$txtReqPath.Parent = $grpReq

$btnBrowseReq = New-Object System.Windows.Forms.Button
$btnBrowseReq.Text = "Browse..."
$btnBrowseReq.Location = New-Object System.Drawing.Point(455, 145)
$btnBrowseReq.Size = New-Object System.Drawing.Size(70, 24)
$btnBrowseReq.Parent = $grpReq

$btnGenCSR = New-Object System.Windows.Forms.Button
$btnGenCSR.Text = "Generate CSR"
$btnGenCSR.Location = New-Object System.Drawing.Point(545, 144)
$btnGenCSR.Size = New-Object System.Drawing.Size(130, 26)
$btnGenCSR.Parent = $grpReq

$lblCSRResult = New-Object System.Windows.Forms.Label
$lblCSRResult.Text = ""
$lblCSRResult.Location = New-Object System.Drawing.Point(15, 175)
$lblCSRResult.Width = 650
$lblCSRResult.ForeColor = [System.Drawing.Color]::DarkGreen
$lblCSRResult.Parent = $grpReq

$saveFileDialog = New-Object System.Windows.Forms.SaveFileDialog
$saveFileDialog.Filter = "Certificate Request (*.req)|*.req|All files (*.*)|*.*"
$saveFileDialog.Title = "Save Certificate Request File"
$btnBrowseReq.Add_Click({
    if ($saveFileDialog.ShowDialog() -eq "OK") {
        $txtReqPath.Text = $saveFileDialog.FileName
    }
})

$btnGenCSR.Add_Click({
    $lblCSRResult.ForeColor = [System.Drawing.Color]::DarkGreen
    $lblCSRResult.Text = ""
    if (-not $global:Connected -or -not $global:Session) {
        $lblCSRResult.ForeColor = [System.Drawing.Color]::Red
        $lblCSRResult.Text = "You must be connected to Exchange to generate a CSR."
        return
    }
    $friendly = $txtFriendly.Text
    $subject = $txtCN.Text
    $san = $txtSAN.Text
    $reqPath = $txtReqPath.Text
    $isWildcard = $rbWildcard.Checked
    if (-not $friendly -or -not $subject -or -not $reqPath) {
        $lblCSRResult.ForeColor = [System.Drawing.Color]::Red
        $lblCSRResult.Text = "All fields except SAN are required."
        return
    }
    if ($isWildcard -and ($subject -notmatch "^CN=\*\.") ) {
        $lblCSRResult.ForeColor = [System.Drawing.Color]::Red
        $lblCSRResult.Text = "Wildcard subject must begin with CN=*."
        return
    }
    try {
        $params = @{
            FriendlyName = $friendly
            SubjectName  = $subject
            Path         = $reqPath
            PrivateKeyExportable = $true
            KeySize = 2048
        }
        if ($isWildcard) {
            # For wildcard, SAN is ignored
        } elseif ($san) {
            $params['DomainName'] = ($subject -replace '^CN=',''), ($san -split ",")
        } else {
            $params['DomainName'] = ($subject -replace '^CN=','')
        }
        $result = Invoke-Command -Session $global:Session -ScriptBlock {
            param($p)
            New-ExchangeCertificate @p
        } -ArgumentList $params
        $lblCSRResult.ForeColor = [System.Drawing.Color]::DarkGreen
        $lblCSRResult.Text = "CSR generated and saved to $reqPath"
    } catch {
        $lblCSRResult.ForeColor = [System.Drawing.Color]::Red
        $lblCSRResult.Text = "Error: $($_.Exception.Message)"
    }
})

###################################################
# 4. Complete Certificate Request Section         #
###################################################

$grpComplete = New-Object System.Windows.Forms.GroupBox
$grpComplete.Text = "Complete Certificate Request"
$grpComplete.Location = New-Object System.Drawing.Point(20, 380)
$grpComplete.Size = New-Object System.Drawing.Size(710, 110)
$form.Controls.Add($grpComplete)

$lblP7B = New-Object System.Windows.Forms.Label
$lblP7B.Text = "Certificate Chain File (.p7b):"
$lblP7B.Location = New-Object System.Drawing.Point(15, 35)
$lblP7B.AutoSize = $true
$lblP7B.Parent = $grpComplete

$txtP7BPath = New-Object System.Windows.Forms.TextBox
$txtP7BPath.Location = New-Object System.Drawing.Point(180, 33)
$txtP7BPath.Width = 320
$txtP7BPath.Parent = $grpComplete

$btnBrowseP7B = New-Object System.Windows.Forms.Button
$btnBrowseP7B.Text = "Browse..."
$btnBrowseP7B.Location = New-Object System.Drawing.Point(510, 32)
$btnBrowseP7B.Size = New-Object System.Drawing.Size(70, 24)
$btnBrowseP7B.Parent = $grpComplete

$btnComplete = New-Object System.Windows.Forms.Button
$btnComplete.Text = "Complete Request"
$btnComplete.Location = New-Object System.Drawing.Point(600, 31)
$btnComplete.Size = New-Object System.Drawing.Size(100, 26)
$btnComplete.Parent = $grpComplete

$lblCompleteResult = New-Object System.Windows.Forms.Label
$lblCompleteResult.Text = ""
$lblCompleteResult.Location = New-Object System.Drawing.Point(15, 60)
$lblCompleteResult.Width = 670
$lblCompleteResult.ForeColor = [System.Drawing.Color]::DarkGreen
$lblCompleteResult.Parent = $grpComplete

$openFileDialog = New-Object System.Windows.Forms.OpenFileDialog
$openFileDialog.Filter = "Certificate Chain (*.p7b)|*.p7b|All files (*.*)|*.*"
$openFileDialog.Title = "Select Certificate Chain File"
$btnBrowseP7B.Add_Click({
    if ($openFileDialog.ShowDialog() -eq "OK") {
        $txtP7BPath.Text = $openFileDialog.FileName
    }
})

$btnComplete.Add_Click({
    $lblCompleteResult.Text = ""
    $lblCompleteResult.ForeColor = [System.Drawing.Color]::DarkGreen
    if (-not $global:Connected -or -not $global:Session) {
        $lblCompleteResult.ForeColor = [System.Drawing.Color]::Red
        $lblCompleteResult.Text = "You must be connected to Exchange to complete a request."
        return
    }
    $p7bPath = $txtP7BPath.Text
    if (-not $p7bPath -or -not (Test-Path $p7bPath)) {
        $lblCompleteResult.ForeColor = [System.Drawing.Color]::Red
        $lblCompleteResult.Text = "Please select a valid .p7b certificate chain file."
        return
    }
    try {
        $fileBytes = [System.IO.File]::ReadAllBytes($p7bPath)
        $imported = Invoke-Command -Session $global:Session -ScriptBlock {
            param($bytes)
            Import-ExchangeCertificate -FileData $bytes
        } -ArgumentList $fileBytes
        if ($imported -is [array]) { $thumbprint = $imported[0].Thumbprint } else { $thumbprint = $imported.Thumbprint }
        $global:ImportedThumbprint = $thumbprint
        $lblCompleteResult.ForeColor = [System.Drawing.Color]::DarkGreen
        $lblCompleteResult.Text = "Certificate completed and imported with Thumbprint: $thumbprint"
    } catch {
        $lblCompleteResult.ForeColor = [System.Drawing.Color]::Red
        $lblCompleteResult.Text = "Error: $($_.Exception.Message)"
    }
})

###################################################
# 5. Assign Certificate Section                  #
###################################################

$grpAssign = New-Object System.Windows.Forms.GroupBox
$grpAssign.Text = "Assign Imported Certificate to Services"
$grpAssign.Location = New-Object System.Drawing.Point(20, 500)
$grpAssign.Size = New-Object System.Drawing.Size(710, 120)
$form.Controls.Add($grpAssign)

$lblAssignInfo = New-Object System.Windows.Forms.Label
$lblAssignInfo.Text = "Assign the most recently imported certificate to:"
$lblAssignInfo.Location = New-Object System.Drawing.Point(15, 25)
$lblAssignInfo.AutoSize = $true
$lblAssignInfo.Parent = $grpAssign

$chkIIS = New-Object System.Windows.Forms.CheckBox
$chkIIS.Text = "IIS"
$chkIIS.Location = New-Object System.Drawing.Point(320, 25)
$chkIIS.Checked = $true
$chkIIS.AutoSize = $true
$chkIIS.BackColor = [System.Drawing.Color]::Transparent
$chkIIS.Parent = $grpAssign

$chkSMTP = New-Object System.Windows.Forms.CheckBox
$chkSMTP.Text = "SMTP"
$chkSMTP.Location = New-Object System.Drawing.Point(370, 25)
$chkSMTP.Checked = $true
$chkSMTP.AutoSize = $true
$chkSMTP.BackColor = [System.Drawing.Color]::Transparent
$chkSMTP.Parent = $grpAssign

$chkIMAP = New-Object System.Windows.Forms.CheckBox
$chkIMAP.Text = "IMAP"
$chkIMAP.Location = New-Object System.Drawing.Point(440, 25)
$chkIMAP.Checked = $false
$chkIMAP.AutoSize = $true
$chkIMAP.BackColor = [System.Drawing.Color]::Transparent
$chkIMAP.Parent = $grpAssign

$chkPOP = New-Object System.Windows.Forms.CheckBox
$chkPOP.Text = "POP"
$chkPOP.Location = New-Object System.Drawing.Point(510, 25)
$chkPOP.Checked = $false
$chkPOP.AutoSize = $true
$chkPOP.BackColor = [System.Drawing.Color]::Transparent
$chkPOP.Parent = $grpAssign

$chkForceAssign = New-Object System.Windows.Forms.CheckBox
$chkForceAssign.Text = "Force overwrite existing service assignments"
$chkForceAssign.Location = New-Object System.Drawing.Point(320, 50)
$chkForceAssign.Width = 300
$chkForceAssign.AutoSize = $true
$chkForceAssign.BackColor = [System.Drawing.Color]::Transparent
$chkForceAssign.Parent = $grpAssign

$btnAssign = New-Object System.Windows.Forms.Button
$btnAssign.Text = "Assign Certificate"
$btnAssign.Location = New-Object System.Drawing.Point(600, 20)
$btnAssign.Size = New-Object System.Drawing.Size(100, 26)
$btnAssign.Parent = $grpAssign

$lblAssignResult = New-Object System.Windows.Forms.Label
$lblAssignResult.Text = ""
$lblAssignResult.Location = New-Object System.Drawing.Point(15, 80)
$lblAssignResult.Width = 670
$lblAssignResult.ForeColor = [System.Drawing.Color]::DarkGreen
$lblAssignResult.Parent = $grpAssign

$btnAssign.Add_Click({
    $lblAssignResult.Text = ""
    $lblAssignResult.ForeColor = [System.Drawing.Color]::DarkGreen
    if (-not $global:Connected -or -not $global:Session) {
        $lblAssignResult.ForeColor = [System.Drawing.Color]::Red
        $lblAssignResult.Text = "You must be connected to Exchange to assign a certificate."
        return
    }
    if (-not $global:ImportedThumbprint) {
        $lblAssignResult.ForeColor = [System.Drawing.Color]::Red
        $lblAssignResult.Text = "No imported certificate thumbprint found. You must complete or import a certificate first."
        return
    }
    $services = @()
    if ($chkIIS.Checked)  { $services += "IIS" }
    if ($chkSMTP.Checked){ $services += "SMTP" }
    if ($chkIMAP.Checked){ $services += "IMAP" }
    if ($chkPOP.Checked) { $services += "POP" }
    if ($services.Count -eq 0) {
        $lblAssignResult.ForeColor = [System.Drawing.Color]::Red
        $lblAssignResult.Text = "Please select at least one service."
        return
    }
    $svcText = $services -join ","
    $force = $chkForceAssign.Checked
    try {
        $result = Invoke-Command -Session $global:Session -ScriptBlock {
            param($tp, $svc, $frc)
            if ($frc) {
                Enable-ExchangeCertificate -Thumbprint $tp -Services $svc -Force
            } else {
                Enable-ExchangeCertificate -Thumbprint $tp -Services $svc
            }
        } -ArgumentList $global:ImportedThumbprint, $svcText, $force
        $lblAssignResult.ForeColor = [System.Drawing.Color]::DarkGreen
        $lblAssignResult.Text = "Certificate assigned to: $svcText"
    } catch {
        $lblAssignResult.ForeColor = [System.Drawing.Color]::Red
        $lblAssignResult.Text = "Error: $($_.Exception.Message)"
    }
})

###################################################
# 6. Export Certificate Button and Form           #
###################################################

$btnExportCerts = New-Object System.Windows.Forms.Button
$btnExportCerts.Text = "Export Certificates"
$btnExportCerts.Size = New-Object System.Drawing.Size(180, 32)
$btnExportCerts.Location = New-Object System.Drawing.Point(550, 635)
$form.Controls.Add($btnExportCerts)

function Show-ExportCertsForm {
    param(
        [System.Management.Automation.Runspaces.PSSession]$Session,
        [string]$Server
    )
    Add-Type -AssemblyName System.Windows.Forms

    $exportForm = New-Object System.Windows.Forms.Form
    $exportForm.Text = "Exchange Certificate Export"
    $exportForm.Size = New-Object System.Drawing.Size(600, 400)
    $exportForm.StartPosition = "CenterScreen"
    $exportForm.MaximizeBox = $false

    $lvCerts = New-Object System.Windows.Forms.ListView
    $lvCerts.Location = New-Object System.Drawing.Point(20, 20)
    $lvCerts.Size = New-Object System.Drawing.Size(540, 210)
    $lvCerts.View = 'Details'
    $lvCerts.FullRowSelect = $true
    $lvCerts.MultiSelect = $false
    $lvCerts.Columns.Add("Thumbprint", 140)
    $lvCerts.Columns.Add("Friendly Name", 160)
    $lvCerts.Columns.Add("Subject", 220)
    $exportForm.Controls.Add($lvCerts)

    $btnExport = New-Object System.Windows.Forms.Button
    $btnExport.Text = "Export Selected"
    $btnExport.Location = New-Object System.Drawing.Point(440, 240)
    $btnExport.Size = New-Object System.Drawing.Size(120, 32)
    $btnExport.Enabled = $false
    $exportForm.Controls.Add($btnExport)

    $lblStatus = New-Object System.Windows.Forms.Label
    $lblStatus.Text = ""
    $lblStatus.Location = New-Object System.Drawing.Point(20, 300)
    $lblStatus.Width = 540
    $lblStatus.ForeColor = [System.Drawing.Color]::Blue
    $exportForm.Controls.Add($lblStatus)

    $lvCerts.Items.Clear()
    $lblStatus.Text = ""
    try {
        $exchangeCerts = Invoke-Command -Session $Session -ScriptBlock { param($s) Get-ExchangeCertificate -Server $s } -ArgumentList $Server
        if (-not $exchangeCerts) {
            throw "No certificates found or unable to connect."
        }
        foreach ($ex in $exchangeCerts) {
            $item = New-Object System.Windows.Forms.ListViewItem($ex.Thumbprint)
            $item.SubItems.Add($ex.FriendlyName)
            $item.SubItems.Add($ex.Subject)
            $lvCerts.Items.Add($item) | Out-Null
        }
        $lblStatus.ForeColor = [System.Drawing.Color]::DarkGreen
        $lblStatus.Text = "Certificates listed for $Server."
    } catch {
        $lblStatus.ForeColor = [System.Drawing.Color]::Red
        $lblStatus.Text = "Error: $($_.Exception.Message)"
    }

    $lvCerts.Add_SelectedIndexChanged({
        $btnExport.Enabled = ($lvCerts.SelectedItems.Count -eq 1)
    })

    function Prompt-ForPassword {
        $pwdForm = New-Object System.Windows.Forms.Form
        $pwdForm.Text = "Enter PFX Password"
        $pwdForm.Size = New-Object System.Drawing.Size(350,150)
        $pwdForm.StartPosition = "CenterParent"
        $pwdForm.MaximizeBox = $false
        $pwdForm.MinimizeBox = $false

        $lbl = New-Object System.Windows.Forms.Label
        $lbl.Text = "Password for exported PFX file:"
        $lbl.AutoSize = $true
        $lbl.Location = New-Object System.Drawing.Point(12, 15)
        $pwdForm.Controls.Add($lbl)

        $txt = New-Object System.Windows.Forms.TextBox
        $txt.Location = New-Object System.Drawing.Point(15, 45)
        $txt.Width = 300
        $txt.UseSystemPasswordChar = $true
        $pwdForm.Controls.Add($txt)

        $btnOK = New-Object System.Windows.Forms.Button
        $btnOK.Text = "OK"
        $btnOK.Location = New-Object System.Drawing.Point(130, 80)
        $btnOK.DialogResult = [System.Windows.Forms.DialogResult]::OK
        $pwdForm.AcceptButton = $btnOK
        $pwdForm.Controls.Add($btnOK)

        $pwdForm.Topmost = $true
        if ($pwdForm.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
            return $txt.Text
        } else {
            return $null
        }
    }

    $btnExport.Add_Click({
        $lblStatus.Text = ""
        if ($lvCerts.SelectedItems.Count -ne 1) {
            $lblStatus.ForeColor = [System.Drawing.Color]::Red
            $lblStatus.Text = "Please select a certificate to export."
            return
        }
        $thumbprint = $lvCerts.SelectedItems[0].Text
        $saveDialog = New-Object System.Windows.Forms.SaveFileDialog
        $saveDialog.Title = "Export Certificate as PFX"
        $saveDialog.Filter = "PFX file (*.pfx)|*.pfx|All files (*.*)|*.*"
        $saveDialog.FileName = "$thumbprint.pfx"
        if ($saveDialog.ShowDialog() -ne "OK") { return }
        $filePath = $saveDialog.FileName

        $pwd = Prompt-ForPassword
        if (-not $pwd) {
            $lblStatus.ForeColor = [System.Drawing.Color]::Red
            $lblStatus.Text = "Export cancelled (password not entered)."
            return
        }
        try {
            $securePwd = ConvertTo-SecureString $pwd -AsPlainText -Force
            $cert = Invoke-Command -Session $Session -ScriptBlock {
                param($tp, $pwd)
                Export-ExchangeCertificate -Thumbprint $tp -BinaryEncoded -Password $pwd
            } -ArgumentList $thumbprint, $securePwd
            [System.IO.File]::WriteAllBytes($filePath, $cert.FileData)
            $lblStatus.ForeColor = [System.Drawing.Color]::DarkGreen
            $lblStatus.Text = "Exported to $filePath"
        } catch {
            $lblStatus.ForeColor = [System.Drawing.Color]::Red
            $lblStatus.Text = "Export failed: $($_.Exception.Message)"
        }
    })

    [void]$exportForm.ShowDialog()
}

$btnExportCerts.Add_Click({
    if (-not $txtUsername.Text -or -not $txtPassword.Text -or -not $txtServer.Text) {
        [System.Windows.Forms.MessageBox]::Show(
            "Please enter your Exchange connection details and click Connect first.",
            "Connection Required",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        return
    }
    if (-not $global:Connected -or -not $global:Session) {
        [System.Windows.Forms.MessageBox]::Show(
            "Please connect to Exchange first.",
            "Connection Required",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        return
    }
    Show-ExportCertsForm -Session $global:Session -Server $txtServer.Text
})

###################################################
# 7. Import Certificate Button and Form           #
###################################################

$btnImportCert = New-Object System.Windows.Forms.Button
$btnImportCert.Text = "Import Certificate"
$btnImportCert.Size = New-Object System.Drawing.Size(180, 32)
$btnImportCert.Location = New-Object System.Drawing.Point(340, 635)
$form.Controls.Add($btnImportCert)

function Show-ImportCertForm {
    param(
        [System.Management.Automation.Runspaces.PSSession]$Session
    )
    Add-Type -AssemblyName System.Windows.Forms

    $importForm = New-Object System.Windows.Forms.Form
    $importForm.Text = "Import Certificate to Exchange Server(s)"
    $importForm.Size = New-Object System.Drawing.Size(600, 460)
    $importForm.StartPosition = "CenterScreen"
    $importForm.MaximizeBox = $false

    $lblServers = New-Object System.Windows.Forms.Label
    $lblServers.Text = "Select Exchange Server(s):"
    $lblServers.Location = New-Object System.Drawing.Point(20, 20)
    $lblServers.AutoSize = $true
    $importForm.Controls.Add($lblServers)

    $lvServers = New-Object System.Windows.Forms.ListView
    $lvServers.Location = New-Object System.Drawing.Point(20, 45)
    $lvServers.Size = New-Object System.Drawing.Size(340, 120)
    $lvServers.View = 'Details'
    $lvServers.FullRowSelect = $true
    $lvServers.MultiSelect = $true
    $lvServers.Columns.Add("Server Name", 320)
    $importForm.Controls.Add($lvServers)

    $lblPfx = New-Object System.Windows.Forms.Label
    $lblPfx.Text = "PFX File:"
    $lblPfx.Location = New-Object System.Drawing.Point(20, 180)
    $lblPfx.AutoSize = $true
    $importForm.Controls.Add($lblPfx)

    $txtPfx = New-Object System.Windows.Forms.TextBox
    $txtPfx.Location = New-Object System.Drawing.Point(90, 178)
    $txtPfx.Width = 300
    $importForm.Controls.Add($txtPfx)

    $btnBrowsePfx = New-Object System.Windows.Forms.Button
    $btnBrowsePfx.Text = "Browse..."
    $btnBrowsePfx.Location = New-Object System.Drawing.Point(400, 177)
    $btnBrowsePfx.Size = New-Object System.Drawing.Size(80, 24)
    $importForm.Controls.Add($btnBrowsePfx)

    $lblPwd = New-Object System.Windows.Forms.Label
    $lblPwd.Text = "PFX Password:"
    $lblPwd.Location = New-Object System.Drawing.Point(20, 215)
    $lblPwd.AutoSize = $true
    $importForm.Controls.Add($lblPwd)

    $txtPwd = New-Object System.Windows.Forms.TextBox
    $txtPwd.Location = New-Object System.Drawing.Point(120, 213)
    $txtPwd.Width = 180
    $txtPwd.UseSystemPasswordChar = $true
    $importForm.Controls.Add($txtPwd)

    $lblServices = New-Object System.Windows.Forms.Label
    $lblServices.Text = "Assign to Services:"
    $lblServices.Location = New-Object System.Drawing.Point(20, 250)
    $lblServices.AutoSize = $true
    $importForm.Controls.Add($lblServices)

    $chkIISimp = New-Object System.Windows.Forms.CheckBox
    $chkIISimp.Text = "IIS"
    $chkIISimp.Location = New-Object System.Drawing.Point(160, 248)
    $chkIISimp.AutoSize = $true
    $chkIISimp.Checked = $true
    $importForm.Controls.Add($chkIISimp)

    $chkSMTPimp = New-Object System.Windows.Forms.CheckBox
    $chkSMTPimp.Text = "SMTP"
    $chkSMTPimp.Location = New-Object System.Drawing.Point(210, 248)
    $chkSMTPimp.AutoSize = $true
    $chkSMTPimp.Checked = $true
    $importForm.Controls.Add($chkSMTPimp)

    $chkIMAPimp = New-Object System.Windows.Forms.CheckBox
    $chkIMAPimp.Text = "IMAP"
    $chkIMAPimp.Location = New-Object System.Drawing.Point(280, 248)
    $chkIMAPimp.AutoSize = $true
    $importForm.Controls.Add($chkIMAPimp)

    $chkPOPimp = New-Object System.Windows.Forms.CheckBox
    $chkPOPimp.Text = "POP"
    $chkPOPimp.Location = New-Object System.Drawing.Point(350, 248)
    $chkPOPimp.AutoSize = $true
    $importForm.Controls.Add($chkPOPimp)

    $btnImport = New-Object System.Windows.Forms.Button
    $btnImport.Text = "Import and Assign"
    $btnImport.Location = New-Object System.Drawing.Point(210, 300)
    $btnImport.Size = New-Object System.Drawing.Size(150, 36)
    $btnImport.Enabled = $false
    $importForm.Controls.Add($btnImport)

    $lblStatus = New-Object System.Windows.Forms.Label
    $lblStatus.Text = ""
    $lblStatus.Location = New-Object System.Drawing.Point(20, 350)
    $lblStatus.Width = 530
    $lblStatus.ForeColor = [System.Drawing.Color]::Blue
    $importForm.Controls.Add($lblStatus)

    $openPfxDialog = New-Object System.Windows.Forms.OpenFileDialog
    $openPfxDialog.Filter = "PFX File (*.pfx)|*.pfx|All Files (*.*)|*.*"
    $btnBrowsePfx.Add_Click({
        if ($openPfxDialog.ShowDialog() -eq "OK") {
            $txtPfx.Text = $openPfxDialog.FileName
        }
    })

    $lvServers.Items.Clear()
    try {
        $servers = Invoke-Command -Session $Session -ScriptBlock { Get-ExchangeServer | Select-Object -ExpandProperty Name }
        foreach ($svr in $servers) {
            $item = New-Object System.Windows.Forms.ListViewItem($svr)
            $lvServers.Items.Add($item) | Out-Null
        }
    } catch {
        $lblStatus.ForeColor = [System.Drawing.Color]::Red
        $lblStatus.Text = "Failed to list Exchange servers: $($_.Exception.Message)"
    }

    $validateFields = {
        $btnImport.Enabled =
            ($lvServers.SelectedItems.Count -gt 0) -and
            ($txtPfx.Text -and (Test-Path $txtPfx.Text)) -and
            ($txtPwd.Text.Length -gt 0) -and
            ($chkIISimp.Checked -or $chkSMTPimp.Checked -or $chkIMAPimp.Checked -or $chkPOPimp.Checked)
    }
    $lvServers.Add_SelectedIndexChanged($validateFields)
    $txtPfx.Add_TextChanged($validateFields)
    $txtPwd.Add_TextChanged($validateFields)
    $chkIISimp.Add_CheckedChanged($validateFields)
    $chkSMTPimp.Add_CheckedChanged($validateFields)
    $chkIMAPimp.Add_CheckedChanged($validateFields)
    $chkPOPimp.Add_CheckedChanged($validateFields)

    $btnImport.Add_Click({
        $lblStatus.Text = ""
        $selectedServers = @($lvServers.SelectedItems | ForEach-Object { $_.Text })
        $file = $txtPfx.Text
        $pwd = $txtPwd.Text
        $services = @()
        if ($chkIISimp.Checked) { $services += "IIS" }
        if ($chkSMTPimp.Checked) { $services += "SMTP" }
        if ($chkIMAPimp.Checked) { $services += "IMAP" }
        if ($chkPOPimp.Checked) { $services += "POP" }
        if (-not (Test-Path $file)) {
            $lblStatus.ForeColor = [System.Drawing.Color]::Red
            $lblStatus.Text = "PFX file not found."
            return
        }
        if ($selectedServers.Count -eq 0) {
            $lblStatus.ForeColor = [System.Drawing.Color]::Red
            $lblStatus.Text = "Please select at least one Exchange server."
            return
        }
        if ($services.Count -eq 0) {
            $lblStatus.ForeColor = [System.Drawing.Color]::Red
            $lblStatus.Text = "Please select at least one service."
            return
        }
        $securePwd = ConvertTo-SecureString $pwd -AsPlainText -Force
        $fileData = $null
        try {
            $fileData = [System.IO.File]::ReadAllBytes($file)
        } catch {
            $lblStatus.ForeColor = [System.Drawing.Color]::Red
            $lblStatus.Text = "Could not read PFX file: $($_.Exception.Message)"
            return
        }
        $success = $true
        $importedThumbprints = @()

        foreach ($server in $selectedServers) {
            try {
                $importResult = Invoke-Command -Session $Session -ScriptBlock {
                    param($fdata, $pwd, $srv)
                    Import-ExchangeCertificate -FileData $fdata -Password $pwd -Server $srv
                } -ArgumentList $fileData, $securePwd, $server

                if ($importResult -is [array]) { $thumb = $importResult[0].Thumbprint } else { $thumb = $importResult.Thumbprint }
                $importedThumbprints += $thumb

                $svcString = ($services -join ",")
                Invoke-Command -Session $Session -ScriptBlock {
                    param($t, $svc, $srv)
                    Enable-ExchangeCertificate -Thumbprint $t -Services $svc -Server $srv -Force
                } -ArgumentList $thumb, $svcString, $server

                $lblStatus.ForeColor = [System.Drawing.Color]::DarkGreen
                $lblStatus.Text = "Imported and assigned certificate to $(${selectedServers} -join ', ')"
            } catch {
                $success = $false
                $lblStatus.ForeColor = [System.Drawing.Color]::Red
                $lblStatus.Text = "Failed for ${server}: $($_.Exception.Message)"
                break
            }
        }
        if ($success -and $importedThumbprints.Count -gt 0) {
            [System.Windows.Forms.MessageBox]::Show(
                "Imported and assigned certificate to: $(${selectedServers} -join ', ')" + "`nThumbprints: $($importedThumbprints -join ', ')",
                "Import Success",
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Information
            ) | Out-Null
            $importForm.Close()
        }
    })

    [void]$importForm.ShowDialog()
}

$btnImportCert.Add_Click({
    if (-not $global:Connected -or -not $global:Session) {
        [System.Windows.Forms.MessageBox]::Show(
            "Please connect to Exchange first.",
            "Connection Required",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        return
    }
    Show-ImportCertForm -Session $global:Session
})

###################################################
# 8. Main Form Show Event                        #
###################################################

$form.Add_Shown({
    try {
        $existingSessions = Get-PSSession | Where-Object { $_.ConfigurationName -eq "Microsoft.Exchange" }
        foreach ($sess in $existingSessions) {
            try { Remove-PSSession $sess -ErrorAction SilentlyContinue } catch {}
        }
    } catch {}
    $btnDisconnect.Enabled = $false
    $global:Connected = $false
    $global:Session = $null
    if (Get-Command -Name UpdateOpMode -ErrorAction SilentlyContinue) { UpdateOpMode }
})

[void]$form.ShowDialog()
