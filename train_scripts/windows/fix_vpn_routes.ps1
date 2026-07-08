# Обход Amnezia: lab_comp (192.168.194.x) и h200 (10.x через OpenVPN).
# Запускать ПОСЛЕ подключения Amnezia (и OpenVPN для h200).
# Правый клик -> "Запуск от имени администратора", или:
#   powershell -ExecutionPolicy Bypass -File train_scripts\windows\fix_vpn_routes.ps1

$ErrorActionPreference = 'SilentlyContinue'

function Get-LanAdapter {
    Get-NetAdapter | Where-Object {
        $_.Status -eq 'Up' -and
        $_.InterfaceDescription -match 'Wi-Fi|Ethernet|Realtek' -and
        $_.Name -notmatch 'ZeroTier|vEthernet|Amnezia|Wintun|outline|Hyper-V|Bluetooth|Direct'
    } | Select-Object -First 1
}

function Get-OpenVpnAdapter {
    Get-NetAdapter | Where-Object {
        $_.Status -eq 'Up' -and $_.Name -match 'outline|tap'
    } | Select-Object -First 1
}

Write-Host "[fix_vpn] LAN adapter + routes for lab_comp / h200"

$wifi = Get-LanAdapter
if ($wifi) {
    Remove-NetRoute -DestinationPrefix '192.168.194.0/24' -InterfaceAlias 'AmneziaVPN' -Confirm:$false
    New-NetRoute -DestinationPrefix '192.168.194.0/24' -InterfaceIndex $wifi.InterfaceIndex `
        -NextHop '0.0.0.0' -RouteMetric 1 -PolicyStore ActiveStore | Out-Null
    # постоянный маршрут (переживает перезагрузку)
    route -p add 192.168.194.0 mask 255.255.255.0 0.0.0.0 if $($wifi.InterfaceIndex) metric 1 | Out-Null
    Write-Host "[fix_vpn] 192.168.194.0/24 -> $($wifi.Name) (if $($wifi.InterfaceIndex))"
}

$ovpn = Get-OpenVpnAdapter
if ($ovpn) {
    Remove-NetRoute -DestinationPrefix '10.0.0.0/9','10.128.0.0/9','10.0.116.11/32' `
        -InterfaceAlias 'AmneziaVPN' -Confirm:$false
    $tapIp = (Get-NetIPAddress -InterfaceIndex $ovpn.InterfaceIndex -AddressFamily IPv4 |
        Where-Object { $_.IPAddress -notlike '169.*' } | Select-Object -First 1).IPAddress
    if ($tapIp) {
        $parts = $tapIp.Split('.')
        $gw = "$($parts[0]).$($parts[1]).$($parts[2]).1"
        New-NetRoute -DestinationPrefix '10.0.116.11/32' -InterfaceIndex $ovpn.InterfaceIndex `
            -NextHop $gw -RouteMetric 1 -PolicyStore ActiveStore | Out-Null
        Write-Host "[fix_vpn] 10.0.116.11 -> OpenVPN ($gw via $($ovpn.Name))"
    }
} else {
    Write-Host "[fix_vpn] OpenVPN not connected - skip h200 route"
}

Write-Host "[fix_vpn] test:"
ssh -o ConnectTimeout=5 -o BatchMode=yes lab_comp "echo lab_comp_OK" 2>&1
ssh -o ConnectTimeout=5 -o BatchMode=yes h200 "echo h200_OK" 2>&1
