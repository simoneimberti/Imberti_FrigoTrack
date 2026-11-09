#!/bin/bash
# Dichiarazione dell'interprete bash per lo script

FILE="rifornimenti.csv"
# Definisce il nome del file CSV da analizzare

read -p "inserisci targa " targa
# Mostra il prompt "inserisci targa" e aspetta l'input dell'utente
# L'input viene salvato nella variabile 'targa'

read -p "inserisci mese " mese
# Mostra il prompt "inserisci mese" e aspetta l'input dell'utente
# L'input viene salvato nella variabile 'mese'

# =============================================
# INIZIO CONTROLLO CONDIZIONI
# =============================================

if [ -z "$targa" ] && [ -z "$mese" ]; then
# SE: la variabile targa è VUOTA (-z) E (&&) la variabile mese è VUOTA (-z)
# allora esegui il blocco seguente

media_generale=$(awk -F';' 'NR>1 {somma +=($4/$3); n++} END {if (n>0) print somma/n; else print "nessun elemento"}' "$FILE")
# COMANDO AWK per calcolare la media generale:
# -F';'               → Imposta il separatore di campo come punto e virgola
# NR>1                → Salta la prima riga (intestazione)
# {somma +=$4; n++}   → Per ogni riga: aggiungi il campo 4 (km) alla somma, incrementa il contatore
# END                 → Blocco eseguito dopo aver processato tutto il file
# if (n>0)            → Se sono stati processati record
#   print somma/n     → Calcola e stampa la media (totale_km / numero_record)
# else                → Altrimenti
#   print "nessun elemento" → Stampa messaggio di errore
# "$FILE"             → File su cui eseguire awk
# $(...)              → Capture output del comando e salva in variabile media_generale

echo "Media generale: $media_generale km/l"
# Stampa il risultato della media generale con unità di misura

# =============================================
elif [ -z "$targa" ]; then
# ALTRIMENTI SE (elif): solo la targa è VUOTA (-z) → utente ha inserito solo il mese

media_mese=$(awk -F';' -v mes="$mese" 'NR>1 && substr($2,4,2)==mes {coca +=($4/$3); n++} END {if (n>0) print coca/n; else print "nessun elemento"}' "$FILE")
# COMANDO AWK per calcolare la media per mese:
# -F';'                           → Separatore punto e virgola
# -v mes="$mese"                  → Passa la variabile bash 'mese' a awk come 'mes'
# NR>1 && substr($2,6,2)==mes     → Filtra: salta intestazione E estrai mese dalla data
#                                   substr($2,6,2) → dalla data "2025-01-07" estrae "01"
# {coca +=$4; n++}                → Accumula km nel campo 4 e conta record
# END {if (n>0) print coca/n...}  → Calcola media o stampa messaggio errore

echo "Media per mese $mese: $media_mese km/l"
# Stampa il risultato della media mensile

# =============================================
elif [ -z "$mese" ]; then
# ALTRIMENTI SE (elif): solo il mese è VUOTO (-z) → utente ha inserito solo la targa

media_targa=$(awk -F';' -v tar="$targa" 'NR>1 && $1==tar {coc +=($4/$3); n++} END {if (n>0) print coc/n; else print "nessun elemento"}' "$FILE")
# COMANDO AWK per calcolare la media per targa:
# -F';'                   → Separatore punto e virgola
# -v tar="$targa"         → Passa la variabile bash 'targa' a awk come 'tar'
# NR>1 && $1==tar         → Filtra: salta intestazione E campo1 (targa) uguale a input
# {coc +=$4; n++}         → Accumula km nel campo 4 e conta record
# END {if (n>0) print coc/n...} → Calcola media o stampa messaggio errore

echo "Media per targa $targa: $media_targa km/l"
# Stampa il risultato della media per targa

# =============================================
else
# ALTRIMENTI (else): entrambi i parametri sono stati inseriti

media_combinata=$(awk -F';' -v tar="$targa" -v mes="$mese" 'NR>1 && $1==tar && substr($2,4,2)==mes {coc +=($4/$3); n++} END {if (n>0) print coc/n; else print "nessun elemento"}' "$FILE")
# COMANDO AWK per calcolare la media combinata targa+mese:
# -F';'                                       → Separatore punto e virgola
# -v tar="$targa" -v mes="$mese"              → Passa entrambe le variabili a awk
# NR>1 && $1==tar && substr($2,6,2)==mes      → Filtra combinato:
#                                               - Salta intestazione
#                                               - Targa corrispondente
#                                               - Mese corrispondente
# {coc +=$4; n++}                             → Accumula km e conta record
# END {if (n>0) print coc/n...}               → Calcola media o errore

echo "Media per targa $targa nel mese $mese: $media_combinata km/l"
# Stampa il risultato della media combinata

fi
# FINE del blocco condizionale if-elif-else
