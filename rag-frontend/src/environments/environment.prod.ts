export const environment = {
  production: true,
  // Vuoto: le chiamate usano percorsi relativi (es. /api/ask), stessa origine del frontend.
  // IIS instrada /api/* verso il backend locale tramite reverse proxy (vedi public/web.config).
  apiUrl: ''
};
