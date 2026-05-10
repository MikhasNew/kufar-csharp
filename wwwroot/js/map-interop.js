window.MapInterop = {
    map: null,
    markersLayer: null,
    polygonsLayer: null,
    poiLayer: null,

    init: function (elementId) {
        if (this.map) { this.map.remove(); this.map = null; }
        this.map = L.map(elementId).setView([53.9006, 27.5590], 12);
        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19, attribution: '© OpenStreetMap'
        }).addTo(this.map);

        this.markersLayer = L.layerGroup().addTo(this.map);
        this.polygonsLayer = L.layerGroup().addTo(this.map);
        this.poiLayer = L.layerGroup().addTo(this.map);

        // Панель слоёв
        L.control.layers(null, {
            "Объявления": this.markersLayer,
            "Районы": this.polygonsLayer,
            "POI": this.poiLayer
        }, { collapsed: false }).addTo(this.map);
    },

    addMarkers: function (listings) {
        if (!this.markersLayer) return;
        this.markersLayer.clearLayers();
        var colors = { 'Buy': '#22c55e', 'Hold': '#f59e0b', 'Avoid': '#ef4444' };

        listings.forEach(function (l) {
            if (!l.lat || !l.lon) return;
            var color = colors[l.recommendation] || '#6b7280';
            var icon = L.divIcon({
                className: 'custom-marker',
                html: '<div style="background:' + color + ';width:12px;height:12px;border-radius:50%;border:2px solid #fff;box-shadow:0 1px 3px rgba(0,0,0,0.3);"></div>',
                iconSize: [12, 12], iconAnchor: [6, 6]
            });
            var popup = '<div style="min-width:200px">' +
                '<b>' + (l.title || '').substring(0, 60) + '</b><br>' +
                '<b>$' + (l.price || 0).toLocaleString() + '</b> · ' + (l.pricePerSqm || 0) + ' $/м²<br>' +
                (l.rooms || '?') + ' комн. · ' + (l.area || '?') + ' м²<br>' +
                'Район: ' + (l.district || '?') + '<br>' +
                'Скор: <b>' + (l.score || 0) + '</b> — <span style="color:' + color + ';font-weight:bold">' + (l.recommendation || '?') + '</span><br>' +
                (l.url ? '<a href="' + l.url + '" target="_blank">Kufar ↗</a>' : '') + '</div>';
            L.marker([l.lat, l.lon], { icon: icon }).bindPopup(popup).addTo(this.markersLayer);
        }.bind(this));
    },

    addPolygons: function (polygons) {
        if (!this.polygonsLayer) return;
        this.polygonsLayer.clearLayers();
        var dc = {
            'Центральный':'#ef4444','Советский':'#f97316','Первомайский':'#eab308',
            'Партизанский':'#22c55e','Заводской':'#14b8a6','Ленинский':'#3b82f6',
            'Октябрьский':'#8b5cf6','Московский':'#ec4899','Фрунзенский':'#6366f1'
        };
        for (var name in polygons) {
            var coords = polygons[name].map(function(p){ return [p[0], p[1]]; });
            var c = dc[name] || '#6b7280';
            L.polygon(coords, { color:c, weight:2, fillOpacity:0.08, fillColor:c })
                .bindTooltip(name, { permanent:false, direction:'center' })
                .addTo(this.polygonsLayer);
        }
    },

    addPoi: function (poi) {
        if (!this.poiLayer) return;
        this.poiLayer.clearLayers();

        var icons = {
            metro: function() { return L.divIcon({
                className:'', iconSize:[22,22], iconAnchor:[11,11],
                html:'<div style="background:#e11d48;width:22px;height:22px;border-radius:50%;border:3px solid #fff;display:flex;align-items:center;justify-content:center;color:#fff;font-size:11px;font-weight:bold;box-shadow:0 2px 6px rgba(0,0,0,0.4)">M</div>'
            }); },
            park: function() { return L.divIcon({
                className:'', iconSize:[20,20], iconAnchor:[10,10],
                html:'<div style="background:#22c55e;width:20px;height:20px;border-radius:50%;border:2px solid #fff;display:flex;align-items:center;justify-content:center;font-size:12px;box-shadow:0 1px 4px rgba(0,0,0,0.3)">🌳</div>'
            }); },
            water: function() { return L.divIcon({
                className:'', iconSize:[20,20], iconAnchor:[10,10],
                html:'<div style="background:#3b82f6;width:20px;height:20px;border-radius:50%;border:2px solid #fff;display:flex;align-items:center;justify-content:center;font-size:12px;box-shadow:0 1px 4px rgba(0,0,0,0.3)">💧</div>'
            }); },
            forest: function() { return L.divIcon({
                className:'', iconSize:[18,18], iconAnchor:[9,9],
                html:'<div style="background:#15803d;width:18px;height:18px;border-radius:50%;border:2px solid #fff;display:flex;align-items:center;justify-content:center;font-size:10px;box-shadow:0 1px 4px rgba(0,0,0,0.3)">🌲</div>'
            }); }
        };

        var radiuses = { metro: 800, park: 500, water: 300, forest: 500 };
        var radiusColors = { metro:'#e11d48', park:'#22c55e', water:'#3b82f6', forest:'#15803d' };

        poi.forEach(function(p) {
            var iconFn = icons[p.type];
            if (!iconFn) return;
            L.marker([p.lat, p.lon], { icon: iconFn() })
                .bindTooltip(p.name + ' (' + p.type + ')', { direction:'top', offset:[0,-12] })
                .addTo(this.poiLayer);

            if (p.type === 'metro') {
                L.circle([p.lat, p.lon], {
                    radius: radiuses[p.type], color: radiusColors[p.type],
                    weight:1, fillColor: radiusColors[p.type], fillOpacity:0.04
                }).addTo(this.poiLayer);
            }
        }.bind(this));
    },

    invalidateSize: function () {
        if (this.map) setTimeout(function(){ this.map.invalidateSize(); }.bind(this), 100);
    }
};
