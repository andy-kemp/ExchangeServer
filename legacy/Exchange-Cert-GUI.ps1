Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.Text = "Exchange Certificate Request Generator"
$form.Size = New-Object System.Drawing.Size(760, 530)
$form.StartPosition = "CenterScreen"
$form.MaximizeBox = $false

# --- Connect to Exchange section ---
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
$btnConnect.Location = New-Object System.Drawing.Point(570, 60)
$btnConnect.Width = 80
$form.Controls.Add($btnConnect)

$btnDisconnect = New-Object System.Windows.Forms.Button
$btnDisconnect.Text = "Disconnect"
$btnDisconnect.Location = New-Object System.Drawing.Point(570, 95)
$btnDisconnect.Width = 80
$btnDisconnect.Enabled = $false
$form.Controls.Add($btnDisconnect)

$lblConnectResult = New-Object System.Windows.Forms.Label
$lblConnectResult.Text = ""
$lblConnectResult.Location = New-Object System.Drawing.Point(40, 100)
$lblConnectResult.Width = 600
$lblConnectResult.ForeColor = [System.Drawing.Color]::Blue
$form.Controls.Add($lblConnectResult)

$global:Connected = $false
$global:Session = $null

# --- Main operation mode selection ---
$lblOpMode = New-Object System.Windows.Forms.Label
$lblOpMode.Text = "Operation:"
$lblOpMode.Location = New-Object System.Drawing.Point(40, 130)
$lblOpMode.AutoSize = $true
$form.Controls.Add($lblOpMode)

$rbReq = New-Object System.Windows.Forms.RadioButton
$rbReq.Text = "New Certificate Request"
$rbReq.Location = New-Object System.Drawing.Point(150, 128)
$rbReq.Width = 180
$rbReq.Checked = $true
$form.Controls.Add($rbReq)

$rbComplete = New-Object System.Windows.Forms.RadioButton
$rbComplete.Text = "Complete Certificate Request"
$rbComplete.Location = New-Object System.Drawing.Point(340, 128)
$rbComplete.Width = 210
$form.Controls.Add($rbComplete)

# --- Certificate Request Section ---
$grpReq = New-Object System.Windows.Forms.GroupBox
$grpReq.Text = "Generate New Certificate Request"
$grpReq.Location = New-Object System.Drawing.Point(20, 160)
$grpReq.Size = New-Object System.Drawing.Size(710, 200)
$form.Controls.Add($grpReq)

# Certificate Type (SAN or Wildcard) -- On one line!
$lblCertType = New-Object System.Windows.Forms.Label
$lblCertType.Text = "Certificate Type:"
$lblCertType.Location = New-Object System.Drawing.Point(15, 28)
$lblCertType.AutoSize = $true
$grpReq.Controls.Add($lblCertType)

$rbSAN = New-Object System.Windows.Forms.RadioButton
$rbSAN.Text = "SAN Certificate"
$rbSAN.Location = New-Object System.Drawing.Point(145, 26)
$rbSAN.Width = 120
$rbSAN.AutoSize = $false
$rbSAN.Checked = $true
$grpReq.Controls.Add($rbSAN)

$rbWildcard = New-Object System.Windows.Forms.RadioButton
$rbWildcard.Text = "Wildcard Certificate"
$rbWildcard.Location = New-Object System.Drawing.Point(275, 26)
$rbWildcard.Width = 160
$rbWildcard.AutoSize = $false
$grpReq.Controls.Add($rbWildcard)

# Friendly Name
$lblFriendly = New-Object System.Windows.Forms.Label
$lblFriendly.Text = "Friendly Name:"
$lblFriendly.Location = New-Object System.Drawing.Point(15, 58)
$lblFriendly.AutoSize = $true
$grpReq.Controls.Add($lblFriendly)

$txtFriendly = New-Object System.Windows.Forms.TextBox
$txtFriendly.Location = New-Object System.Drawing.Point(145, 56)
$txtFriendly.Width = 200
$grpReq.Controls.Add($txtFriendly)

# Subject Name (hostname only)
$lblCN = New-Object System.Windows.Forms.Label
$lblCN.Text = "Subject Name (e.g. CN=mail.domain.com):"
$lblCN.Location = New-Object System.Drawing.Point(15, 88)
$lblCN.AutoSize = $true
$grpReq.Controls.Add($lblCN)

$txtCN = New-Object System.Windows.Forms.TextBox
$txtCN.Location = New-Object System.Drawing.Point(285, 86)
$txtCN.Width = 220
$grpReq.Controls.Add($txtCN)

# SANs
$lblSAN = New-Object System.Windows.Forms.Label
$lblSAN.Text = "Additional Domain Names (CSV):"
$lblSAN.Location = New-Object System.Drawing.Point(15, 118)
$lblSAN.AutoSize = $true
$grpReq.Controls.Add($lblSAN)

$txtSAN = New-Object System.Windows.Forms.TextBox
$txtSAN.Location = New-Object System.Drawing.Point(225, 116)
$txtSAN.Width = 300
$grpReq.Controls.Add($txtSAN)

# Request file path and browse
$lblReqPath = New-Object System.Windows.Forms.Label
$lblReqPath.Text = "Request File (.req):"
$lblReqPath.Location = New-Object System.Drawing.Point(15, 148)
$lblReqPath.AutoSize = $true
$grpReq.Controls.Add($lblReqPath)

$txtReqPath = New-Object System.Windows.Forms.TextBox
$txtReqPath.Location = New-Object System.Drawing.Point(145, 146)
$txtReqPath.Width = 300
$grpReq.Controls.Add($txtReqPath)

$btnBrowseReq = New-Object System.Windows.Forms.Button
$btnBrowseReq.Text = "Browse..."
$btnBrowseReq.Location = New-Object System.Drawing.Point(455, 145)
$btnBrowseReq.Width = 70
$grpReq.Controls.Add($btnBrowseReq)

$btnGenCSR = New-Object System.Windows.Forms.Button
$btnGenCSR.Text = "Generate CSR"
$btnGenCSR.Location = New-Object System.Drawing.Point(545, 144)
$btnGenCSR.Width = 130
$grpReq.Controls.Add($btnGenCSR)

$lblCSRResult = New-Object System.Windows.Forms.Label
$lblCSRResult.Text = ""
$lblCSRResult.Location = New-Object System.Drawing.Point(15, 175)
$lblCSRResult.Width = 650
$lblCSRResult.ForeColor = [System.Drawing.Color]::DarkGreen
$grpReq.Controls.Add($lblCSRResult)

# --- Complete Certificate Request Section ---
$grpComplete = New-Object System.Windows.Forms.GroupBox
$grpComplete.Text = "Complete Certificate Request"
$grpComplete.Location = New-Object System.Drawing.Point(20, 370)
$grpComplete.Size = New-Object System.Drawing.Size(710, 85)
$form.Controls.Add($grpComplete)

$lblP7B = New-Object System.Windows.Forms.Label
$lblP7B.Text = "Certificate Chain File (.p7b):"
$lblP7B.Location = New-Object System.Drawing.Point(15, 35)
$lblP7B.AutoSize = $true
$grpComplete.Controls.Add($lblP7B)

$txtP7BPath = New-Object System.Windows.Forms.TextBox
$txtP7BPath.Location = New-Object System.Drawing.Point(180, 33)
$txtP7BPath.Width = 320
$grpComplete.Controls.Add($txtP7BPath)

$btnBrowseP7B = New-Object System.Windows.Forms.Button
$btnBrowseP7B.Text = "Browse..."
$btnBrowseP7B.Location = New-Object System.Drawing.Point(510, 32)
$btnBrowseP7B.Width = 70
$grpComplete.Controls.Add($btnBrowseP7B)

$btnComplete = New-Object System.Windows.Forms.Button
$btnComplete.Text = "Complete Request"
$btnComplete.Location = New-Object System.Drawing.Point(600, 31)
$btnComplete.Width = 100
$grpComplete.Controls.Add($btnComplete)

$lblCompleteResult = New-Object System.Windows.Forms.Label
$lblCompleteResult.Text = ""
$lblCompleteResult.Location = New-Object System.Drawing.Point(15, 60)
$lblCompleteResult.Width = 670
$lblCompleteResult.ForeColor = [System.Drawing.Color]::DarkGreen
$grpComplete.Controls.Add($lblCompleteResult)

$global:CurrentCSR = ""

$saveFileDialog = New-Object System.Windows.Forms.SaveFileDialog
$saveFileDialog.Filter = "Certificate Request (*.req)|*.req|All files (*.*)|*.*"
$saveFileDialog.Title = "Save Certificate Request File"

$openFileDialog = New-Object System.Windows.Forms.OpenFileDialog
$openFileDialog.Filter = "Certificate Chain (*.p7b)|*.p7b|All files (*.*)|*.*"
$openFileDialog.Title = "Select Certificate Chain File"

$btnBrowseReq.Add_Click({
    if ($saveFileDialog.ShowDialog() -eq "OK") {
        $txtReqPath.Text = $saveFileDialog.FileName
    }
})

$btnBrowseP7B.Add_Click({
    if ($openFileDialog.ShowDialog() -eq "OK") {
        $txtP7BPath.Text = $openFileDialog.FileName
    }
})

# Enable/Disable UI elements based on operation mode
function UpdateOpMode {
    if ($rbReq.Checked) {
        $grpReq.Enabled = $true
        $grpComplete.Enabled = $false
        $btnGenCSR.Enabled = $true
        $btnComplete.Enabled = $false
    } else {
        $grpReq.Enabled = $false
        $grpComplete.Enabled = $true
        $btnGenCSR.Enabled = $false
        $btnComplete.Enabled = $true
    }
}
$rbReq.Add_CheckedChanged({ UpdateOpMode })
$rbComplete.Add_CheckedChanged({ UpdateOpMode })

# On cert type change, update Subject Name example/enable SAN field
$rbSAN.Add_CheckedChanged({
    if ($rbSAN.Checked) {
        $lblCN.Text = "Subject Name (e.g. CN=mail.domain.com):"
        $lblSAN.Enabled = $true
        $txtSAN.Enabled = $true
        $txtCN.Text = ""
        $txtSAN.Text = ""
    }
})
$rbWildcard.Add_CheckedChanged({
    if ($rbWildcard.Checked) {
        $lblCN.Text = "Subject Name (e.g. CN=*.domain.com):"
        $lblSAN.Enabled = $false
        $txtSAN.Enabled = $false
        $txtCN.Text = ""
        $txtSAN.Text = ""
    }
})

function EnsureExchangeConnection {
    if ($global:Connected -and $global:Session) { return $true }
    $username = $txtUsername.Text
    $password = $txtPassword.Text
    $server = $txtServer.Text
    if (-not $username -or -not $password -or -not $server) {
        [System.Windows.Forms.MessageBox]::Show("Please complete all connection fields.", "Error", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
        return $false
    }
    # Kill any existing Microsoft.Exchange sessions
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
    EnsureExchangeConnection | Out-Null
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

$btnGenCSR.Add_Click({
    $lblCSRResult.Text = ""
    $global:CurrentCSR = ""
    if (-not (EnsureExchangeConnection)) { return }
    $friendly = $txtFriendly.Text
    $subjectName = $txtCN.Text.Trim()
    $reqPath = $txtReqPath.Text
    $server = $txtServer.Text.Trim()
    $sans = $txtSAN.Text
    $isWildcard = $rbWildcard.Checked

    if (-not $reqPath) {
        $lblCSRResult.ForeColor = [System.Drawing.Color]::Red
        $lblCSRResult.Text = "Please provide a path for the request file (.req)."
        return
    }
    if (-not $friendly -or -not $subjectName -or -not $server) {
        $lblCSRResult.ForeColor = [System.Drawing.Color]::Red
        $lblCSRResult.Text = "Please provide Friendly Name, Subject Name, and Server."
        return
    }

    # Wildcard format check
    if ($isWildcard -and $subjectName -notmatch '\*\.') {
        $lblCSRResult.ForeColor = [System.Drawing.Color]::Red
        $lblCSRResult.Text = "For Wildcard, use CN=*.domain.com as Subject Name."
        return
    }

    # Remove CN= if user forgot (for both SAN and Wildcard, always prepend)
    if ($subjectName -and $subjectName -notmatch '^CN=') {
        $subjectName = "CN=$subjectName"
    }

    # Check for pending request on specific server
    try {
        $pending = Get-ExchangeCertificate -Server $server | Where-Object { $_.Status -eq "PendingRequest" }
        if ($pending) {
            $msg = "A pending certificate request exists on $server. It will be removed automatically to allow a new request."
            [System.Windows.Forms.MessageBox]::Show($msg,"Pending Request",[System.Windows.Forms.MessageBoxButtons]::OK,[System.Windows.Forms.MessageBoxIcon]::Warning) | Out-Null
            foreach ($cert in $pending) {
                Remove-ExchangeCertificate -Thumbprint $cert.Thumbprint -Server $server -Confirm:$false
            }
        }
    } catch {}

    $tempDerPath = [System.IO.Path]::GetTempFileName() + ".der"

    if ($isWildcard) {
        try {
            $txtrequest = New-ExchangeCertificate -PrivateKeyExportable $True -GenerateRequest -FriendlyName $friendly -SubjectName $subjectName -Server $server
            if ($txtrequest -match "-----BEGIN NEW CERTIFICATE REQUEST-----") {
                # It's already PEM, save as is
                Set-Content -Path $reqPath -Value $txtrequest -Encoding ascii
                $lblCSRResult.ForeColor = [System.Drawing.Color]::DarkGreen
                $lblCSRResult.Text = "Wildcard CSR saved to $reqPath (PEM format, ready for CA)."
            } else {
                # Fallback: treat as base64 and wrap in PEM
                $pemBody = ($txtrequest -split "(.{1,64})" | Where-Object {$_ -ne ""}) -join "`n"
                $pemText = "-----BEGIN NEW CERTIFICATE REQUEST-----`n$pemBody`n-----END NEW CERTIFICATE REQUEST-----`n"
                Set-Content -Path $reqPath -Value $pemText -Encoding ascii
                $lblCSRResult.ForeColor = [System.Drawing.Color]::DarkGreen
                $lblCSRResult.Text = "Wildcard CSR saved to $reqPath (PEM format, ready for CA)."
            }
        } catch {
            $lblCSRResult.ForeColor = [System.Drawing.Color]::Red
            $lblCSRResult.Text = "Error creating wildcard CSR: $($_.Exception.Message)"
        }
    } else {
        $params = @{
            FriendlyName = $friendly
            SubjectName = $subjectName
            PrivateKeyExportable = $True
            GenerateRequest = $True
            BinaryEncoded = $True
            Server = $server
        }
        if ($sans) { $params.DomainName = $sans -split "," | ForEach-Object { $_.Trim() } }
        try {
            $certRequest = New-ExchangeCertificate @params
            if ($certRequest -and $certRequest.FileData) {
                [System.IO.File]::WriteAllBytes($tempDerPath, $certRequest.FileData)
                # Convert DER to PEM and write to user-requested .req file
                try {
                    $derBytes = [System.IO.File]::ReadAllBytes($tempDerPath)
                    $base64 = [System.Convert]::ToBase64String($derBytes)
                    $pemBody = ($base64 -split "(.{1,64})" | Where-Object {$_ -ne ""}) -join "`n"
                    $pemText = "-----BEGIN NEW CERTIFICATE REQUEST-----`n$pemBody`n-----END NEW CERTIFICATE REQUEST-----`n"
                    Set-Content -Path $reqPath -Value $pemText -Encoding ascii
                    Remove-Item $tempDerPath -ErrorAction SilentlyContinue
                    $lblCSRResult.ForeColor = [System.Drawing.Color]::DarkGreen
                    $lblCSRResult.Text = "CSR saved to $reqPath (PEM format, ready for CA)."
                } catch {
                    $lblCSRResult.ForeColor = [System.Drawing.Color]::Red
                    $lblCSRResult.Text = "DER to PEM conversion failed! $($_.Exception.Message)"
                }
            } else {
                $lblCSRResult.ForeColor = [System.Drawing.Color]::Red
                $lblCSRResult.Text = "No result. Check parameters and permissions."
            }
        } catch {
            $lblCSRResult.ForeColor = [System.Drawing.Color]::Red
            $lblCSRResult.Text = "Error: $($_.Exception.Message)"
        }
    }
})

$btnComplete.Add_Click({
    $lblCompleteResult.Text = ""
    if (-not (EnsureExchangeConnection)) { return }
    $p7bPath = $txtP7BPath.Text
    if (-not $p7bPath -or -not (Test-Path $p7bPath)) {
        $lblCompleteResult.ForeColor = [System.Drawing.Color]::Red
        $lblCompleteResult.Text = "Please select a valid .p7b file."
        return
    }
    try {
        Import-ExchangeCertificate -FileData ([System.IO.File]::ReadAllBytes($p7bPath)) | Out-Null
        $lblCompleteResult.ForeColor = [System.Drawing.Color]::DarkGreen
        $lblCompleteResult.Text = "Certificate chain imported successfully."
    } catch {
        $lblCompleteResult.ForeColor = [System.Drawing.Color]::Red
        $lblCompleteResult.Text = "Import failed: $($_.Exception.Message)"
    }
})

# On form load: clean up sessions and set Disconnect disabled and update UI
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
    UpdateOpMode
})

[void]$form.ShowDialog()
