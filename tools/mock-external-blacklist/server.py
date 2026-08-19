#!/usr/bin/env python3
"""Servidor mock de la API externa de reportes de robo, solo para probar
ExternalBlacklistSyncService / HttpExternalBlacklistSource en local antes de tener la URL
real del cliente. Usa solo la librería estándar de Python (cualquier Python 3 sirve).

Uso:
    python server.py [puerto]   # default 5566

Expone GET /api/reportes-robo y valida "Authorization: Bearer <TOKEN_ESPERADO>" (ajusta
TOKEN_ESPERADO abajo para que coincida con lo que pongas en
`dotnet user-secrets set "ExternalBlacklist:BearerToken" ...`). Es un archivo de prueba,
no parte del sistema real — bórralo cuando ya no lo necesites.
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer

TOKEN_ESPERADO = "test-token-123"

REGISTROS = [
    {"placa": "ABC1234", "numeroReporte": "REP-2026-001", "fechaReporte": "2026-08-15", "busquedaActiva": True, "imagenCarro": "", "modelo": "Aveo", "anio": 2019, "vendor": "Chevrolet", "color": "Rojo", "clase": "Carro", "marcasUotros": ""},
    {"placa": "XYZ9988", "numeroReporte": "REP-2026-002", "fechaReporte": "2026-08-10", "busquedaActiva": True, "imagenCarro": "", "modelo": "CBR600", "anio": 2021, "vendor": "Honda", "color": "Negro", "clase": "Motocicleta", "marcasUotros": "Placa robada en gasolinera"},
    {"placa": "TEST5566", "numeroReporte": "REP-2026-000", "fechaReporte": "2026-01-01", "busquedaActiva": False, "imagenCarro": "", "modelo": "Corolla", "anio": 2020, "vendor": "Toyota", "color": "Blanco", "clase": "Carro", "marcasUotros": "Prueba de baja"},
]


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        auth = self.headers.get("Authorization", "")
        if auth != f"Bearer {TOKEN_ESPERADO}":
            self.send_response(401)
            self.end_headers()
            self.wfile.write(b'{"error":"unauthorized"}')
            print(f"[mock] 401 -- Authorization recibido: {auth!r}")
            return

        body = json.dumps(REGISTROS).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
        print(f"[mock] 200 -- {len(REGISTROS)} registro(s) enviados")

    def log_message(self, format, *args):
        pass


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 5566
    print(f"Mock de API externa de blacklist en http://localhost:{port}/api/reportes-robo")
    print(f"Token esperado: Bearer {TOKEN_ESPERADO}")
    HTTPServer(("localhost", port), Handler).serve_forever()
